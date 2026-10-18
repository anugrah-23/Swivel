using System.Text.Json;
using Microsoft.Data.Sqlite;
using Swivel.Core.Jobs;

namespace Swivel.Core.History;

/// <summary>An output as Swivel wrote it; <paramref name="Source"/> is the input it came from.</summary>
public sealed record OutputStamp(string Path, long Size, long LastWriteUtcTicks, string? Source = null)
{
    public bool SameContentAs(OutputStamp other) =>
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) && Size == other.Size && LastWriteUtcTicks == other.LastWriteUtcTicks;
}

public sealed record HistoryEntry(
    Guid GroupId,
    string OperationId,
    string Label,
    DateTimeOffset Finished,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<OutputStamp> Outputs,
    long BytesBefore,
    long BytesAfter,
    bool Undone);

public enum UndoResult { Done, NotFound, AlreadyUndone, NothingToUndo, OutputsChanged }

/// <summary>Sends files to the Recycle Bin (implemented over the shell in Swivel.Native).</summary>
public interface IRecycler
{
    void Recycle(IReadOnlyList<string> paths);
}

/// <summary>Job history and usage ranks in a small SQLite file.</summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS groups (
                id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL,
                label TEXT NOT NULL,
                started INTEGER NOT NULL,
                finished INTEGER NOT NULL,
                inputs TEXT NOT NULL,
                outputs TEXT NOT NULL,
                bytes_before INTEGER NOT NULL,
                bytes_after INTEGER NOT NULL,
                undone INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS groups_finished ON groups(finished DESC);
            CREATE TABLE IF NOT EXISTS usage (
                operation_id TEXT PRIMARY KEY,
                count INTEGER NOT NULL,
                last_used INTEGER NOT NULL);
            """);
    }

    /// <summary>Stores a finished group and bumps the operation's usage rank.</summary>
    public HistoryEntry Record(JobGroupResult result)
    {
        var stamps = result.Outputs.Where(Exists)
            .Select(o => Stamp(o) with { Source = result.Sources.GetValueOrDefault(o) })
            .ToArray();
        long before = result.Inputs.Sum(SizeOf);
        long after = stamps.Sum(s => s.Size);
        var entry = new HistoryEntry(result.GroupId, result.Operation.Id, result.Operation.Label, result.Finished,
            result.Inputs, stamps, before, after, Undone: false);

        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, """
            INSERT OR REPLACE INTO groups (id, operation_id, label, started, finished, inputs, outputs, bytes_before, bytes_after, undone)
            VALUES ($id, $op, $label, $started, $finished, $inputs, $outputs, $before, $after, 0)
            """,
            ("$id", result.GroupId.ToString()), ("$op", result.Operation.Id), ("$label", result.Operation.Label),
            ("$started", result.Started.ToUnixTimeMilliseconds()), ("$finished", result.Finished.ToUnixTimeMilliseconds()),
            ("$inputs", JsonSerializer.Serialize(result.Inputs)), ("$outputs", JsonSerializer.Serialize(stamps)),
            ("$before", before), ("$after", after));
        if (!result.Cancelled && (stamps.Length > 0 || result.Operation.ProducesNoOutput))
        {
            Execute(db, """
                INSERT INTO usage (operation_id, count, last_used) VALUES ($op, 1, $now)
                ON CONFLICT(operation_id) DO UPDATE SET count = count + 1, last_used = $now
                """, ("$op", result.Operation.Id), ("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds()));
        }
        tx.Commit();
        return entry;
    }

    public IReadOnlyDictionary<string, int> UsageCounts()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT operation_id, count FROM usage";
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<string, int>();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public IReadOnlyList<HistoryEntry> Recent(int count)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM groups ORDER BY finished DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        using var reader = cmd.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read()) entries.Add(Read(reader));
        return entries;
    }

    public HistoryEntry? Find(Guid groupId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", groupId.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>True while every remaining output is still exactly what Swivel wrote.</summary>
    public bool CanUndo(Guid groupId) => Check(Find(groupId), out _) == UndoResult.Done;

    /// <summary>
    /// Recycles the group's outputs, but only if none were changed since Swivel wrote them. A Move is
    /// undone by moving each item back to where it came from instead.
    /// </summary>
    public UndoResult Undo(Guid groupId, IRecycler recycler)
    {
        var entry = Find(groupId);
        var status = Check(entry, out var existing);
        if (status != UndoResult.Done) return status;

        if (entry!.OperationId.StartsWith("action.move", StringComparison.Ordinal))
        {
            if (existing.Any(o => o.Source is null || Files.FileOps.Exists(o.Source))) return UndoResult.OutputsChanged;
            foreach (var output in existing) Files.FileOps.Move(output.Path, output.Source!);
        }
        else
        {
            recycler.Recycle(existing.Select(s => s.Path).ToArray());
        }
        using var db = Open();
        Execute(db, "UPDATE groups SET undone = 1 WHERE id = $id", ("$id", groupId.ToString()));
        return UndoResult.Done;
    }

    private static UndoResult Check(HistoryEntry? entry, out IReadOnlyList<OutputStamp> existing)
    {
        existing = [];
        if (entry is null) return UndoResult.NotFound;
        if (entry.Undone) return UndoResult.AlreadyUndone;
        existing = entry.Outputs.Where(o => Exists(o.Path)).ToArray();
        if (existing.Count == 0) return UndoResult.NothingToUndo;
        return existing.All(o => Stamp(o.Path).SameContentAs(o)) ? UndoResult.Done : UndoResult.OutputsChanged;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>A file's size and time; for a folder output, the total and the newest file's time.</summary>
    private static OutputStamp Stamp(string path)
    {
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);
            var files = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
            long ticks = files.Length == 0 ? dir.LastWriteTimeUtc.Ticks : files.Max(f => f.LastWriteTimeUtc.Ticks);
            return new OutputStamp(dir.FullName, files.Sum(f => f.Length) + files.Length, ticks);
        }
        var info = new FileInfo(path);
        return new OutputStamp(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static long SizeOf(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    private static HistoryEntry Read(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        r.GetString(r.GetOrdinal("operation_id")),
        r.GetString(r.GetOrdinal("label")),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("finished"))).ToLocalTime(),
        JsonSerializer.Deserialize<string[]>(r.GetString(r.GetOrdinal("inputs"))) ?? [],
        JsonSerializer.Deserialize<OutputStamp[]>(r.GetString(r.GetOrdinal("outputs"))) ?? [],
        r.GetInt64(r.GetOrdinal("bytes_before")),
        r.GetInt64(r.GetOrdinal("bytes_after")),
        r.GetInt64(r.GetOrdinal("undone")) != 0);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
