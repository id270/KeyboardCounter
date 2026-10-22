using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// 把 ECDICT 的 ecdict.csv（英→中）和可选的 CC-CEDICT（中→英）转成程序用的 dictionary.db
// 用法：EcdictImporter <ecdict.csv> <dictionary.db> [maxFrq] [cedict.txt]
//   maxFrq    可选：只保留词频在该范围内的词条（0 表示不裁剪）。常用词库建议 100000 左右。
//   cedict.txt 可选：CC-CEDICT 解压后的文本（UTF-8），加进来后中文词也能离线查。

if (args.Length < 2)
{
    Console.WriteLine("usage: EcdictImporter <ecdict.csv> <dictionary.db> [maxFrq] [cedict.txt]");
    return 1;
}

var csvPath = args[0];
var dbPath = args[1];
var maxFrq = args.Length > 2 && int.TryParse(args[2], out var f) ? f : 0;
var cedictPath = args.Length > 3 ? args[3] : null;

if (!File.Exists(csvPath))
{
    Console.WriteLine($"csv not found: {csvPath}");
    return 1;
}

Console.WriteLine($"input : {csvPath} ({new FileInfo(csvPath).Length / 1024 / 1024.0:F1} MB)");
Console.WriteLine($"output: {dbPath} (maxFrq={(maxFrq == 0 ? "all" : maxFrq.ToString())})");
Console.WriteLine($"cedict: {(cedictPath == null ? "(skipped)" : cedictPath)}");

if (File.Exists(dbPath))
    File.Delete(dbPath);

var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(directory))
    Directory.CreateDirectory(directory);

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Mode = SqliteOpenMode.ReadWriteCreate
}.ToString();

var sw = Stopwatch.StartNew();
var total = 0;
var kept = 0;
var forms = 0;

using (var connection = new SqliteConnection(connectionString))
{
    connection.Open();

    Execute(connection, """
        PRAGMA journal_mode=OFF;
        PRAGMA synchronous=OFF;
        PRAGMA temp_store=MEMORY;
        CREATE TABLE words (
            word        TEXT PRIMARY KEY COLLATE NOCASE,
            phonetic    TEXT NOT NULL DEFAULT '',
            translation TEXT NOT NULL DEFAULT '',
            pos         TEXT NOT NULL DEFAULT '',
            tag         TEXT NOT NULL DEFAULT '',
            bnc         INTEGER NOT NULL DEFAULT 0,
            frq         INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;
        CREATE INDEX idx_words_frq ON words(frq);
        CREATE TABLE forms (
            form TEXT PRIMARY KEY COLLATE NOCASE,
            word TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE zh (
            hanzi       TEXT PRIMARY KEY COLLATE NOCASE,
            traditional TEXT NOT NULL DEFAULT '',
            pinyin      TEXT NOT NULL DEFAULT '',
            english     TEXT NOT NULL DEFAULT ''
        ) WITHOUT ROWID;
        """);

    using var transaction = connection.BeginTransaction();
    using var wordCommand = connection.CreateCommand();
    wordCommand.Transaction = transaction;
    wordCommand.CommandText =
        "INSERT OR IGNORE INTO words(word, phonetic, translation, pos, tag, bnc, frq) VALUES($w, $p, $t, $pos, $tag, $bnc, $frq);";
    var wordParam = wordCommand.Parameters.Add("$w", SqliteType.Text);
    var phoneticParam = wordCommand.Parameters.Add("$p", SqliteType.Text);
    var translationParam = wordCommand.Parameters.Add("$t", SqliteType.Text);
    var posParam = wordCommand.Parameters.Add("$pos", SqliteType.Text);
    var tagParam = wordCommand.Parameters.Add("$tag", SqliteType.Text);
    var bncParam = wordCommand.Parameters.Add("$bnc", SqliteType.Integer);
    var frqParam = wordCommand.Parameters.Add("$frq", SqliteType.Integer);

    using var formCommand = connection.CreateCommand();
    formCommand.Transaction = transaction;
    formCommand.CommandText = "INSERT OR IGNORE INTO forms(form, word) VALUES($form, $word);";
    var formParam = formCommand.Parameters.Add("$form", SqliteType.Text);
    var formWordParam = formCommand.Parameters.Add("$word", SqliteType.Text);

    using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    // 跳过表头
    reader.ReadLine();

    var lineNumber = 1;
    var batch = 0;

    while (reader.ReadLine() is { } line)
    {
        lineNumber++;
        if (line.Length == 0)
            continue;

        total++;
        var fields = ParseCsvLine(line);
        if (fields.Count < 11)
            continue;

        var word = fields[0].Trim();
        if (word.Length == 0)
            continue;

        var frq = ParseInt(fields[9]);
        var bnc = ParseInt(fields[8]);

        if (maxFrq > 0)
        {
            var isCommon = (frq > 0 && frq <= maxFrq) || (bnc > 0 && bnc <= maxFrq);
            if (!isCommon)
                continue;
        }

        var translation = fields[3];
        if (translation.Length == 0)
            continue;

        // ECDICT 用字面量 \n 表示换行，这里还原成真换行，方便气泡显示多义项
        translation = translation.Replace("\\n", "\n");

        wordParam.Value = word;
        phoneticParam.Value = fields[1].Trim();
        translationParam.Value = translation;
        posParam.Value = fields[4].Trim();
        tagParam.Value = fields[7].Trim();
        bncParam.Value = bnc;
        frqParam.Value = frq;

        try
        {
            wordCommand.ExecuteNonQuery();
            kept++;
        }
        catch (SqliteException)
        {
            // 主键冲突等，跳过
        }

        // exchange: "p:perceived/d:perceived/3:perceives/i:perceiving" 或 "0:perceive/1:dp/..."
        var exchange = fields[10];
        if (exchange.Length > 0)
        {
            foreach (var part in exchange.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1)
                    continue;

                var type = part[..colon];
                var value = part[(colon + 1)..].Trim();
                if (value.Length == 0)
                    continue;

                // 0 表示"当前词的原形"；其余类型是当前词的变形
                var form = type == "0" ? word : value;
                var lemma = type == "0" ? value : word;

                formParam.Value = form;
                formWordParam.Value = lemma;
                try
                {
                    formCommand.ExecuteNonQuery();
                    forms++;
                }
                catch (SqliteException)
                {
                    // 忽略
                }
            }
        }

        if (++batch >= 20000)
        {
            batch = 0;
            Console.WriteLine($"  {lineNumber} lines, kept={kept}, forms={forms}, {sw.Elapsed.TotalSeconds:F1}s");
        }
    }

    transaction.Commit();
}

// 可选：导入 CC-CEDICT（中→英）
var zhCount = 0;
if (!string.IsNullOrEmpty(cedictPath) && File.Exists(cedictPath))
{
    Console.WriteLine($"importing cedict: {cedictPath}");
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    using var transaction = connection.BeginTransaction();
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText =
        "INSERT OR IGNORE INTO zh(hanzi, traditional, pinyin, english) VALUES($hanzi, $trad, $pinyin, $english);";
    var hanziParam = command.Parameters.Add("$hanzi", SqliteType.Text);
    var tradParam = command.Parameters.Add("$trad", SqliteType.Text);
    var pinyinParam = command.Parameters.Add("$pinyin", SqliteType.Text);
    var englishParam = command.Parameters.Add("$english", SqliteType.Text);

    // 形如：漢語 汉语 [han4 yu3] /Chinese language/
    var pattern = new Regex(@"^(\S+)\s+(\S+)\s+\[([^\]]*)\]\s+/(.*)/\s*$", RegexOptions.Compiled);
    var lineNumber = 0;

    using (var reader = new StreamReader(cedictPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
    {
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0 || line[0] == '#')
                continue;

            var match = pattern.Match(line);
            if (!match.Success)
                continue;

            var traditional = match.Groups[1].Value;
            var simplified = match.Groups[2].Value;
            var pinyin = match.Groups[3].Value.Trim();
            var definitions = match.Groups[4].Value
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .ToArray();

            if (definitions.Length == 0)
                continue;

            var english = string.Join("\n", definitions);

            // 繁体和简体都建索引，查哪个都能命中
            foreach (var key in new[] { simplified, traditional }.Distinct())
            {
                hanziParam.Value = key;
                tradParam.Value = traditional;
                pinyinParam.Value = pinyin;
                englishParam.Value = english;
                try
                {
                    command.ExecuteNonQuery();
                    zhCount++;
                }
                catch (SqliteException)
                {
                    // 主键冲突等，跳过
                }
            }
        }
    }

    transaction.Commit();
    Console.WriteLine($"  cedict lines={lineNumber}, rows={zhCount}, {sw.Elapsed.TotalSeconds:F1}s");
}

// 收尾：整理数据库文件
using (var connection = new SqliteConnection(connectionString))
{
    connection.Open();
    Execute(connection, "ANALYZE; VACUUM;");
}

sw.Stop();
var sizeMb = new FileInfo(dbPath).Length / 1024 / 1024.0;
Console.WriteLine($"done: lines={total}, words={kept}, forms={forms}, zh={zhCount}, {sw.Elapsed.TotalSeconds:F1}s, {sizeMb:F1} MB");
return 0;

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static int ParseInt(string value)
{
    return int.TryParse(value.Trim(), out var result) ? result : 0;
}

static List<string> ParseCsvLine(string line)
{
    var fields = new List<string>(13);
    var builder = new StringBuilder();
    var inQuotes = false;

    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];

        if (inQuotes)
        {
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else
            {
                builder.Append(c);
            }
        }
        else if (c == ',')
        {
            fields.Add(builder.ToString());
            builder.Clear();
        }
        else if (c == '"')
        {
            inQuotes = true;
        }
        else
        {
            builder.Append(c);
        }
    }

    fields.Add(builder.ToString());
    return fields;
}
