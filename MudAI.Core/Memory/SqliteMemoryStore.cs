using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Models;

namespace MudAI.Core.Memory;

/// <summary>
/// SQLite-backed memory. A single connection is kept open and all access is serialized
/// through a semaphore (SQLite connections are not safe for concurrent use).
///
/// Thread-safe: all public members may be called from any thread; access is serialized internally.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IAsyncDisposable
{
    private static readonly HashSet<string> KnownTables = ["lessons", "command_knowledge", "rooms", "awareness"];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SqliteMemoryStore> _logger;
    private SqliteConnection? _conn;

    public SqliteMemoryStore(IOptions<MudAiOptions> options, ILogger<SqliteMemoryStore> logger)
    {
        _logger = logger;
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
                        zone        TEXT    NOT NULL DEFAULT '',
                        exits       TEXT    NOT NULL DEFAULT '',
                        notes       TEXT    NOT NULL DEFAULT '',
                        visits      INTEGER NOT NULL DEFAULT 0,
                        updated_at  TEXT    NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS room_exits (
                        from_room   TEXT    NOT NULL,
                        direction   TEXT    NOT NULL,
                        to_room     TEXT    NOT NULL,
                        updated_at  TEXT    NOT NULL,
                        PRIMARY KEY (from_room, direction)
                    );
                    CREATE TABLE IF NOT EXISTS awareness (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        category    TEXT    NOT NULL,
                        subject     TEXT    NOT NULL,
                        fact        TEXT    NOT NULL,
                        confidence  REAL    NOT NULL DEFAULT 0.5,
                        reinforced  INTEGER NOT NULL DEFAULT 1,
                        created_at  TEXT    NOT NULL,
                        updated_at  TEXT    NOT NULL,
                        UNIQUE (category, subject)
                    );
                    CREATE INDEX IF NOT EXISTS ix_awareness_cat_rank
                        ON awareness (category, confidence DESC, reinforced DESC, updated_at DESC);
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Migrate DBs created before the zone/visits columns existed (ADD COLUMN throws if present).
            await MigrateAsync(conn, "ALTER TABLE rooms ADD COLUMN zone TEXT NOT NULL DEFAULT '';", ct);
            await MigrateAsync(conn, "ALTER TABLE rooms ADD COLUMN visits INTEGER NOT NULL DEFAULT 0;", ct);

            _conn = conn;
            _logger.LogInformation("Memory store initialized ({Connection})", _connectionString);
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

    public async Task RecordRoomVisitAsync(string name, string zone, string exits, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rooms (name, zone, exits, notes, visits, updated_at)
                VALUES ($name, $zone, $exits, '', 1, $now)
                ON CONFLICT(name) DO UPDATE SET
                    zone       = CASE WHEN excluded.zone  <> '' THEN excluded.zone  ELSE rooms.zone  END,
                    exits      = CASE WHEN excluded.exits <> '' THEN excluded.exits ELSE rooms.exits END,
                    visits     = rooms.visits + 1,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$zone", zone ?? "");
            cmd.Parameters.AddWithValue("$exits", exits ?? "");
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task RecordExitAsync(string fromRoom, string direction, string toRoom, CancellationToken ct = default)
    {
        fromRoom = fromRoom.Trim();
        direction = direction.Trim();
        toRoom = toRoom.Trim();
        if (fromRoom.Length == 0 || direction.Length == 0 || toRoom.Length == 0) return;

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO room_exits (from_room, direction, to_room, updated_at)
                VALUES ($from, $dir, $to, $now)
                ON CONFLICT(from_room, direction) DO UPDATE SET
                    to_room    = excluded.to_room,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$from", fromRoom);
            cmd.Parameters.AddWithValue("$dir", direction);
            cmd.Parameters.AddWithValue("$to", toRoom);
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<RoomRecall?> GetRoomRecallAsync(string name, CancellationToken ct = default) =>
        ExecuteAsync(async conn =>
        {
            name = name.Trim();
            string zone = "", exits = "";
            int visits = 0;
            bool found = false;

            await using (var roomCmd = conn.CreateCommand())
            {
                roomCmd.CommandText = "SELECT zone, exits, visits FROM rooms WHERE name = $name;";
                roomCmd.Parameters.AddWithValue("$name", name);
                await using var reader = await roomCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    found = true;
                    zone = reader.GetString(0);
                    exits = reader.GetString(1);
                    visits = reader.GetInt32(2);
                }
            }

            if (!found) return (RoomRecall?)null;

            var edges = new List<RoomExit>();
            await using (var exitCmd = conn.CreateCommand())
            {
                exitCmd.CommandText = "SELECT direction, to_room FROM room_exits WHERE from_room = $name ORDER BY direction;";
                exitCmd.Parameters.AddWithValue("$name", name);
                await using var reader = await exitCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    edges.Add(new RoomExit(reader.GetString(0), reader.GetString(1)));
            }

            return new RoomRecall { Name = name, Zone = zone, Exits = exits, Visits = visits, KnownExits = edges };
        }, ct);

    public async Task AddOrReinforceAwarenessAsync(string category, string subject, string fact,
        double confidence = 0.6, CancellationToken ct = default)
    {
        category = AwarenessVocabulary.Normalize(category);
        subject = AwarenessVocabulary.ClampSubject(subject);
        fact = AwarenessVocabulary.ClampFact(fact);
        if (subject.Length == 0 || fact.Length == 0) return; // both required

        await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO awareness (category, subject, fact, confidence, reinforced, created_at, updated_at)
                VALUES ($cat, $subj, $fact, $conf, 1, $now, $now)
                ON CONFLICT(category, subject) DO UPDATE SET
                    fact       = excluded.fact,
                    confidence = MIN(0.99, awareness.confidence + 0.05),
                    reinforced = awareness.reinforced + 1,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$subj", subject);
            cmd.Parameters.AddWithValue("$fact", fact);
            cmd.Parameters.AddWithValue("$conf", Math.Clamp(confidence, 0, 0.99));
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<IReadOnlyList<AwarenessEntry>> GetBalancedAwarenessAsync(int perCategory, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<AwarenessEntry>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH ranked AS (
                    SELECT id, category, subject, fact, confidence, reinforced, created_at, updated_at,
                           ROW_NUMBER() OVER (
                               PARTITION BY category
                               ORDER BY confidence DESC, reinforced DESC, updated_at DESC) AS rn
                    FROM awareness)
                SELECT id, category, subject, fact, confidence, reinforced, created_at, updated_at
                FROM ranked
                WHERE rn <= $per
                ORDER BY category, confidence DESC, reinforced DESC, updated_at DESC;
                """;
            cmd.Parameters.AddWithValue("$per", Math.Max(1, perCategory));
            return await ReadAwarenessAsync(cmd, ct);
        }, ct);

    public Task<IReadOnlyList<AwarenessEntry>> GetAllAwarenessAsync(CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<AwarenessEntry>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, category, subject, fact, confidence, reinforced, created_at, updated_at
                FROM awareness
                ORDER BY category, confidence DESC, reinforced DESC, updated_at DESC;
                """;
            return await ReadAwarenessAsync(cmd, ct);
        }, ct);

    public Task<ZoneAwareness> GetZoneAwarenessAsync(int maxZones, CancellationToken ct = default) =>
        ExecuteAsync(async conn =>
        {
            var zones = new List<ZoneCount>();
            await using (var zoneCmd = conn.CreateCommand())
            {
                zoneCmd.CommandText = """
                    SELECT zone, COUNT(*) AS c FROM rooms
                    WHERE zone <> '' GROUP BY zone ORDER BY c DESC LIMIT $limit;
                    """;
                zoneCmd.Parameters.AddWithValue("$limit", Math.Max(1, maxZones));
                await using var reader = await zoneCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    zones.Add(new ZoneCount(reader.GetString(0), reader.GetInt32(1)));
            }

            int frontier;
            await using (var frontierCmd = conn.CreateCommand())
            {
                // Rooms whose stated exit count (from GMCP, comma-separated) exceeds the edges we have mapped.
                frontierCmd.CommandText = """
                    SELECT COUNT(*) FROM rooms r
                    WHERE r.exits <> ''
                      AND ((LENGTH(r.exits) - LENGTH(REPLACE(r.exits, ',', '')) + 1)
                           > (SELECT COUNT(*) FROM room_exits e WHERE e.from_room = r.name));
                    """;
                frontier = Convert.ToInt32(await frontierCmd.ExecuteScalarAsync(ct));
            }

            return new ZoneAwareness { Zones = zones, FrontierRooms = frontier };
        }, ct);

    private static async Task<IReadOnlyList<AwarenessEntry>> ReadAwarenessAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<AwarenessEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AwarenessEntry
            {
                Id = reader.GetInt64(0),
                Category = reader.GetString(1),
                Subject = reader.GetString(2),
                Fact = reader.GetString(3),
                Confidence = reader.GetDouble(4),
                TimesReinforced = reader.GetInt32(5),
                CreatedAt = ParseDate(reader.GetString(6)),
                UpdatedAt = ParseDate(reader.GetString(7))
            });
        }
        return list;
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
    private static async Task MigrateAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // Column already exists (older ALTER ran before) - safe to ignore.
        }
    }

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
