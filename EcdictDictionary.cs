using System;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KeyboardCounter;

/// <summary>ECDICT 词条</summary>
public sealed class DictionaryEntry
{
    /// <summary>词条（原形）</summary>
    public string Word { get; init; } = "";

    /// <summary>音标</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>中文释义（多义项用换行分隔）</summary>
    public string Translation { get; init; } = "";

    /// <summary>词性</summary>
    public string PartOfSpeech { get; init; } = "";

    /// <summary>考试/词表标签，如 cet4 cet6 ky</summary>
    public string Tags { get; init; } = "";

    /// <summary>当代语料库词频（越小越常用，0 表示未知）</summary>
    public int Frequency { get; init; }

    /// <summary>如果是通过词形变化查到的，这里是查到的变形（例如 perceived）</summary>
    public string MatchedForm { get; init; } = "";

    /// <summary>是否是中文词条（中→英，来源为 CC-CEDICT）</summary>
    public bool IsChinese { get; init; }
}

/// <summary>
/// 离线英汉词典（ECDICT）。
/// 数据文件默认放在程序目录的 dict\dictionary.db，由 tools\EcdictImporter 生成。
/// 查不到就返回 false，由调用方继续走缓存 / 在线接口。
/// </summary>
public sealed class EcdictDictionary : IDisposable
{
    /// <summary>超过这个长度就不再查词典（明显是句子，交给在线翻译）</summary>
    private const int MaxLookupLength = 120;

    /// <summary>中文词最长查这么多个字，再长就交给在线翻译</summary>
    private const int MaxChineseQueryLength = 8;

    private const string SelectColumns = "SELECT word, phonetic, translation, pos, tag, frq FROM words WHERE word = $word";

    private static readonly char[] TrimChars =
    {
        ' ', '\t', '\r', '\n', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}',
        '<', '>', '。', '，', '、', '；', '：', '！', '？', '.', ',', ';', ':', '!', '?', '…'
    };

    private readonly object _sync = new();
    private SqliteConnection? _connection;

    private EcdictDictionary(SqliteConnection connection, string path)
    {
        _connection = connection;
        DatabasePath = path;
    }

    public string DatabasePath { get; }

    public bool IsAvailable => _connection != null;

    /// <summary>打开词典；文件不存在或损坏时返回 null（程序继续用在线接口）</summary>
    public static EcdictDictionary? TryOpen(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                DebugTrace.Log($"dictionary not found: {path}");
                return null;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true
            }.ToString());
            connection.Open();

            // 试查一次，确认表结构可用
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM words LIMIT 1;";
                command.ExecuteScalar();
            }

            DebugTrace.Log($"dictionary opened: {path}");
            return new EcdictDictionary(connection, path);
        }
        catch (Exception ex)
        {
            DebugTrace.Log("dictionary open failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>查询词条；句子、超长文本、查不到都返回 false</summary>
    public bool TryLookup(string? text, out DictionaryEntry entry)
    {
        entry = null!;

        if (_connection == null)
            return false;

        var query = NormalizeQuery(text);
        if (query.Length == 0)
            return false;

        lock (_sync)
        {
            try
            {
                // 中文走 CC-CEDICT 那张表（中→英）
                if (LanguageUtil.ContainsChinese(query))
                {
                    if (query.Length > MaxChineseQueryLength)
                        return false;

                    return TryReadChinese(query, out entry);
                }

                if (query.Length > MaxLookupLength)
                    return false;

                // 1) 直接命中
                if (TryReadWord(query, out entry))
                    return true;

                // 2) 词形变化（perceived -> perceive）
                var lemma = TryReadLemma(query);
                if (!string.IsNullOrEmpty(lemma) && TryReadWord(lemma, out var byForm))
                {
                    entry = new DictionaryEntry
                    {
                        Word = byForm.Word,
                        Phonetic = byForm.Phonetic,
                        Translation = byForm.Translation,
                        PartOfSpeech = byForm.PartOfSpeech,
                        Tags = byForm.Tags,
                        Frequency = byForm.Frequency,
                        MatchedForm = query
                    };
                    return true;
                }

                // 3) 简单词尾还原兜底（词库没收录的变形）
                foreach (var candidate in CandidateLemmas(query))
                {
                    if (!TryReadWord(candidate, out var bySuffix))
                        continue;

                    entry = new DictionaryEntry
                    {
                        Word = bySuffix.Word,
                        Phonetic = bySuffix.Phonetic,
                        Translation = bySuffix.Translation,
                        PartOfSpeech = bySuffix.PartOfSpeech,
                        Tags = bySuffix.Tags,
                        Frequency = bySuffix.Frequency,
                        MatchedForm = query
                    };
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugTrace.Log("dictionary lookup failed: " + ex.Message);
            }
        }

        return false;
    }

    /// <summary>查 CC-CEDICT 的中文词条（中 → 英）</summary>
    private bool TryReadChinese(string hanzi, out DictionaryEntry entry)
    {
        entry = null!;

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT hanzi, pinyin, english FROM zh WHERE hanzi = $hanzi LIMIT 1;";
        command.Parameters.AddWithValue("$hanzi", hanzi);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;

        entry = new DictionaryEntry
        {
            Word = reader.GetString(0),
            Phonetic = reader.GetString(1),
            Translation = reader.GetString(2),
            IsChinese = true
        };

        return true;
    }

    private bool TryReadWord(string word, out DictionaryEntry entry)
    {
        entry = null!;

        using var command = _connection!.CreateCommand();
        command.CommandText = SelectColumns;
        command.Parameters.AddWithValue("$word", word);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;

        entry = new DictionaryEntry
        {
            Word = reader.GetString(0),
            Phonetic = reader.GetString(1),
            Translation = reader.GetString(2),
            PartOfSpeech = reader.GetString(3),
            Tags = reader.GetString(4),
            Frequency = reader.GetInt32(5)
        };

        return true;
    }

    private string? TryReadLemma(string form)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT word FROM forms WHERE form = $form LIMIT 1;";
        command.Parameters.AddWithValue("$form", form);
        return command.ExecuteScalar() as string;
    }

    /// <summary>简单的词尾规则，覆盖词库缺少的词形</summary>
    private static System.Collections.Generic.IEnumerable<string> CandidateLemmas(string word)
    {
        var lower = word.ToLowerInvariant();

        if (lower.Length > 4 && lower.EndsWith("ies", StringComparison.Ordinal))
            yield return lower[..^3] + "y";
        if (lower.Length > 3 && lower.EndsWith("es", StringComparison.Ordinal))
            yield return lower[..^2];
        if (lower.Length > 3 && lower.EndsWith("s", StringComparison.Ordinal) && !lower.EndsWith("ss", StringComparison.Ordinal))
            yield return lower[..^1];
        if (lower.Length > 5 && lower.EndsWith("ing", StringComparison.Ordinal))
        {
            yield return lower[..^3];
            yield return lower[..^3] + "e";
            if (lower.Length > 6 && lower[^4] == lower[^5])
                yield return lower[..^4];
        }
        if (lower.Length > 4 && lower.EndsWith("ed", StringComparison.Ordinal))
        {
            yield return lower[..^2];
            yield return lower[..^1];
            if (lower.Length > 5 && lower[^3] == lower[^4])
                yield return lower[..^3];
        }
        if (lower.Length > 4 && lower.EndsWith("er", StringComparison.Ordinal))
        {
            yield return lower[..^2];
            yield return lower[..^1];
        }
        if (lower.Length > 5 && lower.EndsWith("est", StringComparison.Ordinal))
            yield return lower[..^3];
        if (lower.Length > 3 && lower.EndsWith("'s", StringComparison.Ordinal))
            yield return lower[..^2];
    }

    /// <summary>整理查询串：去掉首尾标点、合并空白</summary>
    private static string NormalizeQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var trimmed = text.Trim(TrimChars);
        if (trimmed.Length == 0)
            return "";

        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                _connection?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _connection = null;
        }

        GC.SuppressFinalize(this);
    }
}
