using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace KeyboardCounter;

/// <summary>
/// 本地翻译缓存（SQLite）。
/// 位置：程序目录下的 cache\translate_cache.db，划词翻译时先查这里，命中就不走接口。
/// </summary>
public sealed class TranslationCache : IDisposable
{
    /// <summary>最多保留的条目数，超出后按最久未使用淘汰</summary>
    private const int MaxEntries = 20000;

    private static readonly string[] Schema =
    {
        "PRAGMA journal_mode=WAL;",
        "PRAGMA synchronous=NORMAL;",
        """
        CREATE TABLE IF NOT EXISTS translations (
            provider   TEXT    NOT NULL,
            source     TEXT    NOT NULL,
            target     TEXT    NOT NULL,
            from_lang  TEXT    NOT NULL DEFAULT 'auto',
            to_lang    TEXT    NOT NULL DEFAULT '',
            hits       INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            used_at    INTEGER NOT NULL,
            PRIMARY KEY (provider, source)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_translations_used ON translations(used_at);"
    };

    private readonly object _sync = new();
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private bool _disabled;

    public TranslationCache(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _databasePath = Path.Combine(directory, "translate_cache.db");

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString());
            _connection.Open();

            foreach (var statement in Schema)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            Prune();
            DebugTrace.Log($"cache ready at {_databasePath}, entries={Count}");
        }
        catch (Exception ex)
        {
            // 缓存不可用不能影响翻译主流程
            _disabled = true;
            _databasePath = "";
            DebugTrace.Log("cache init failed: " + ex);
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
    }

    public string DatabasePath => _databasePath;

    /// <summary>当前缓存条目数（诊断用）</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    using var command = _connection?.CreateCommand();
                    if (command == null)
                        return 0;
                    command.CommandText = "SELECT COUNT(*) FROM translations;";
                    return Convert.ToInt32(command.ExecuteScalar() ?? 0);
                }
                catch
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>查询缓存；命中时返回 true 并累加使用次数</summary>
    public bool TryGet(string provider, string source, out TranslationResult result)
    {
        result = null!;

        if (_disabled || _connection == null || string.IsNullOrEmpty(source))
            return false;

        lock (_sync)
        {
            try
            {
                string? target = null;
                string from = "auto";
                string to = "";

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT target, from_lang, to_lang FROM translations WHERE provider = $provider AND source = $source LIMIT 1;";
                    command.Parameters.AddWithValue("$provider", provider);
                    command.Parameters.AddWithValue("$source", source);

                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        target = reader.GetString(0);
                        from = reader.GetString(1);
                        to = reader.GetString(2);
                    }
                }

                if (string.IsNullOrEmpty(target))
                    return false;

                using (var touch = _connection.CreateCommand())
                {
                    touch.CommandText =
                        "UPDATE translations SET hits = hits + 1, used_at = $now WHERE provider = $provider AND source = $source;";
                    touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    touch.Parameters.AddWithValue("$provider", provider);
                    touch.Parameters.AddWithValue("$source", source);
                    touch.ExecuteNonQuery();
                }

                result = new TranslationResult
                {
                    SourceText = source,
                    TranslatedText = target,
                    From = from,
                    To = to,
                    ProviderName = provider,
                    IsFromCache = true
                };

                return true;
            }
            catch (Exception ex)
            {
                DebugTrace.Log("cache read failed: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>写入缓存（同一 provider + 原文会覆盖旧结果）</summary>
    public void Put(string provider, string source, TranslationResult result)
    {
        if (_disabled || _connection == null)
            return;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(result.TranslatedText))
            return;

        lock (_sync)
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO translations (provider, source, target, from_lang, to_lang, hits, created_at, used_at)
                    VALUES ($provider, $source, $target, $from, $to, 0, $now, $now)
                    ON CONFLICT(provider, source) DO UPDATE SET
                        target    = excluded.target,
                        from_lang = excluded.from_lang,
                        to_lang   = excluded.to_lang,
                        used_at   = excluded.used_at;
                    """;
                command.Parameters.AddWithValue("$provider", provider);
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$target", result.TranslatedText);
                command.Parameters.AddWithValue("$from", string.IsNullOrEmpty(result.From) ? "auto" : result.From);
                command.Parameters.AddWithValue("$to", result.To ?? "");
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                DebugTrace.Log("cache write failed: " + ex.Message);
            }
        }
    }

    /// <summary>超过上限时按最久未使用淘汰</summary>
    private void Prune()
    {
        try
        {
            var count = Count;
            if (count <= MaxEntries)
                return;

            using var command = _connection!.CreateCommand();
            command.CommandText = """
                DELETE FROM translations WHERE rowid IN (
                    SELECT rowid FROM translations ORDER BY used_at LIMIT $excess
                );
                """;
            command.Parameters.AddWithValue("$excess", count - MaxEntries);
            var removed = command.ExecuteNonQuery();
            DebugTrace.Log($"cache pruned {removed} entries");
        }
        catch (Exception ex)
        {
            DebugTrace.Log("cache prune failed: " + ex.Message);
        }
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
            _disabled = true;
        }

        GC.SuppressFinalize(this);
    }
}
