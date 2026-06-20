using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Models;

namespace MudAI.Core.Memory;

/// <summary>
/// SQLite-backed memory. A single connection is kept open and all access is serialized
/// through a semaphore (SQLite connections are not safe for concurrent use).
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IAsyncDisposable
{
    private static readonly HashSet<string> KnownTables = ["lessons", "command_knowledge", "rooms"];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _conn;

    public SqliteMemoryStore(IOptions<MudAiOptions> options)
    {
        string? path = options.Value.MemoryDbPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MudAI");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "memory.db");
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_conn is not null) return;

            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS lessons (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        text        TEXT    NOT NULL UNIQUE,
                        tags        TEXT    NOT NULL DEFAULT '',
                        confidence  REAL    NOT NULL DEFAULT 0.5,
                        reinforced  INTEGER NOT NULL DEFAULT 1,
                        created_at  TEXT    NOT NULL,
                        updated_at  TEXT    NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS command_knowledge (
                        command     TEXT    PRIMARY KEY,
                        success     INTEGER NOT NULL DEFAULT 0,
                        failure     INTEGER NOT NULL DEFAULT 0,
                        notes       TEXT    NOT NULL DEFAULT '',
                        updated_at  TEXT    NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS rooms (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        name        TEXT    NOT NULL UNIQUE,
                        exits       TEXT    NOT NULL DEFAULT '',
                        notes       TEXT    NOT NULL DEFAULT '',
                        updated_at  TEXT    NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            _conn = conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddOrReinforceLessonAsync(string text, string tags = "", double confidence = 0.6, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lessons (text, tags, confidence, reinforced, created_at, updated_at)
                VALUES ($text, $tags, $conf, 1, $now, $now)
                ON CONFLICT(text) DO UPDATE SET
                    reinforced = reinforced + 1,
                    confidence = MIN(0.99, confidence + 0.05),
                    tags       = CASE WHEN excluded.tags <> '' THEN excluded.tags ELSE lessons.tags END,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$tags", tags ?? "");
            cmd.Parameters.AddWithValue("$conf", Math.Clamp(confidence, 0, 0.99));
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<IReadOnlyList<Lesson>> GetTopLessonsAsync(int limit, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<Lesson>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, text, tags, confidence, reinforced, created_at, updated_at
                FROM lessons
                ORDER BY confidence DESC, reinforced DESC, updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

            var list = new List<Lesson>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new Lesson
                {
                    Id = reader.GetInt64(0),
                    Text = reader.GetString(1),
                    Tags = reader.GetString(2),
                    Confidence = reader.GetDouble(3),
                    TimesReinforced = reader.GetInt32(4),
                    CreatedAt = ParseDate(reader.GetString(5)),
                    UpdatedAt = ParseDate(reader.GetString(6))
                });
            }
            return list;
        }, ct);

    public async Task RecordCommandResultAsync(string command, bool success, CancellationToken ct = default)
    {
        command = command.Trim().ToLowerInvariant();
        if (command.Length == 0) return;

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO command_knowledge (command, success, failure, updated_at)
                VALUES ($cmd, $s, $f, $now)
                ON CONFLICT(command) DO UPDATE SET
                    success    = success + $s,
                    failure    = failure + $f,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$cmd", command);
            cmd.Parameters.AddWithValue("$s", success ? 1 : 0);
            cmd.Parameters.AddWithValue("$f", success ? 0 : 1);
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<IReadOnlyList<CommandKnowledge>> GetCommandKnowledgeAsync(int limit, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<CommandKnowledge>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT command, success, failure, notes, updated_at
                FROM command_knowledge
                ORDER BY (success + failure) DESC, updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

            var list = new List<CommandKnowledge>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new CommandKnowledge
                {
                    Command = reader.GetString(0),
                    SuccessCount = reader.GetInt32(1),
                    FailureCount = reader.GetInt32(2),
                    Notes = reader.GetString(3),
                    UpdatedAt = ParseDate(reader.GetString(4))
                });
            }
            return list;
        }, ct);

    public async Task UpsertRoomAsync(string name, string exits, string notes, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rooms (name, exits, notes, updated_at)
                VALUES ($name, $exits, $notes, $now)
                ON CONFLICT(name) DO UPDATE SET
                    exits      = excluded.exits,
                    notes      = excluded.notes,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$exits", exits ?? "");
            cmd.Parameters.AddWithValue("$notes", notes ?? "");
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<int> CountAsync(string table, CancellationToken ct = default)
    {
        if (!KnownTables.Contains(table))
            throw new ArgumentException($"Unknown table '{table}'.", nameof(table));

        return ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table};"; // table validated against whitelist
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }, ct);
    }

    // --- helpers ---

    private async Task ExecuteAsync(Func<SqliteConnection, Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var conn = _conn ?? throw new InvalidOperationException("Memory store not initialized.");
            await action(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var conn = _conn ?? throw new InvalidOperationException("Memory store not initialized.");
            return await action(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Store UTC with a fixed 'Z' offset so lexicographic TEXT ordering == chronological order
    // (a local offset would mis-sort across DST/timezone changes).
    private static string Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.Now;

    public async ValueTask DisposeAsync()
    {
        // Acquire the gate so we never dispose the connection out from under an in-flight op.
        await _gate.WaitAsync();
        try
        {
            if (_conn is not null)
            {
                await _conn.DisposeAsync();
                _conn = null;
            }
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}
