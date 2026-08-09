using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Durable on-disk record of the <c>ssh -M</c> ControlMasters this coordinator
/// process owns: <c>{tempDir}/sdvd-ssh-journal-{pid}.json</c>. The masters are
/// <c>-f</c>-forked and detached, so no process-tree mechanism can reach them —
/// this journal is what teardown paths that never see
/// <see cref="TunnelManager"/>'s in-memory state consume:
/// <list type="bullet">
///   <item>In-process emergency teardown (<see cref="TunnelManager.EmergencyTeardownOwnMasters"/>)
///     reads it on abort paths where <c>Environment.Exit</c> skipped
///     <c>DrainAsync</c>'s <c>finally</c>.</item>
///   <item>The next run's preflight (<see cref="TunnelManager.ReapOrphanedMastersAsync"/>)
///     reaps journals whose coordinator is dead — the cross-run safety net for
///     hard kills, crashes, and power loss.</item>
/// </list>
/// An entry lives from master registration until the master process is
/// <em>confirmed gone</em> — not merely until <c>ssh -O exit</c> was sent.
///
/// The filename is deliberately outside the <c>sdvd-test-ssh-*</c> namespace:
/// <see cref="TunnelManager.CleanupStaleControlSocketsAsync"/> deletes that glob
/// by age, and the journal must outlive the sockets it describes.
/// </summary>
internal static class SshMasterJournal
{
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>One owned master, with everything a foreign process needs to
    /// terminate it: mux reachability (control path + destination), pid-kill
    /// identity guards (WINDOWS pid — mapped out of Cygwin pid space at
    /// registration — plus spawn time + ssh binary), and the <c>-E</c> log for
    /// the death-line diagnostic emit.</summary>
    public sealed class MasterRecord
    {
        [JsonPropertyName("hostId")]
        public string HostId { get; set; } = "";

        [JsonPropertyName("sshDestination")]
        public string SshDestination { get; set; } = "";

        [JsonPropertyName("controlPath")]
        public string ControlPath { get; set; } = "";

        [JsonPropertyName("logPath")]
        public string? LogPath { get; set; }

        [JsonPropertyName("masterPid")]
        public int? MasterPid { get; set; }

        [JsonPropertyName("spawnedAtUtc")]
        public DateTime SpawnedAtUtc { get; set; }

        [JsonPropertyName("sshPath")]
        public string SshPath { get; set; } = "";
    }

    public sealed class JournalFile
    {
        [JsonPropertyName("coordinatorPid")]
        public int CoordinatorPid { get; set; }

        [JsonPropertyName("coordinatorStartTimeUtc")]
        public DateTime CoordinatorStartTimeUtc { get; set; }

        [JsonPropertyName("masters")]
        public List<MasterRecord> Masters { get; set; } = new();
    }

    /// <summary>A parsed journal plus its path, as returned by
    /// <see cref="SnapshotOrphanedJournals"/>.</summary>
    public sealed record OrphanedJournal(string FilePath, JournalFile Journal);

    private const string FilePrefix = "sdvd-ssh-journal-";

    private const int StartTimeToleranceSeconds = 5;

    private static string OwnJournalPath =>
        Path.Combine(Path.GetTempPath(), $"{FilePrefix}{Environment.ProcessId}.json");

    /// <summary>
    /// Upserts (by host id) a master into this process's journal. Called from
    /// <see cref="TunnelManager.RegisterHostMasterAsync"/> — the single site
    /// that sets <c>Owned = true</c> — so both creation paths (preflight and
    /// respawn) keep the journal current for free.
    /// </summary>
    public static void RecordMaster(MasterRecord entry)
    {
        try
        {
            lock (FileLock)
            {
                var journal = ReadOwnJournal();
                if (journal is null)
                {
                    MoveForeignLeakAside();
                    journal = NewOwnJournal();
                }
                journal.Masters.RemoveAll(m =>
                    string.Equals(m.HostId, entry.HostId, StringComparison.Ordinal)
                );
                journal.Masters.Add(entry);
                WriteAtomic(OwnJournalPath, journal);
            }
        }
        catch
        { /* journaling must never fail a master registration */
        }
    }

    /// <summary>
    /// Removes a confirmed-gone master from this process's journal; deletes the
    /// journal file once the last master is gone. The pid + spawn-time pair
    /// guards against erasing a concurrently-respawned successor: the entry is
    /// only removed while it still records the master that was actually
    /// confirmed gone (a live successor's entry must survive so it stays
    /// reapable). Spawn time disambiguates the degraded mode where both pids
    /// are null (pid parse/mapping missed on predecessor and successor alike).
    /// </summary>
    public static void RemoveMaster(string hostId, int? masterPid, DateTime spawnedAtUtc)
    {
        try
        {
            lock (FileLock)
            {
                var journal = ReadOwnJournal();
                if (journal is null)
                {
                    return;
                }

                journal.Masters.RemoveAll(m =>
                    string.Equals(m.HostId, hostId, StringComparison.Ordinal)
                    && m.MasterPid == masterPid
                    && m.SpawnedAtUtc == spawnedAtUtc
                );
                if (journal.Masters.Count == 0)
                {
                    TryDelete(OwnJournalPath);
                }
                else
                {
                    WriteAtomic(OwnJournalPath, journal);
                }
            }
        }
        catch
        { /* best effort */
        }
    }

    /// <summary>
    /// Moves a file squatting our pid path but failing the identity check (a
    /// dead prior coordinator's leak on a recycled pid) to a unique sibling
    /// name instead of letting the first write overwrite it: the leak may
    /// still hold the reap handle for a master that survived its reap
    /// attempt, and the renamed file stays inside the journal glob, so the
    /// orphan reaper keeps retrying it (unparseable debris gets deleted
    /// there instead).
    /// </summary>
    private static void MoveForeignLeakAside()
    {
        try
        {
            if (File.Exists(OwnJournalPath))
            {
                File.Move(
                    OwnJournalPath,
                    Path.Combine(Path.GetTempPath(), $"{FilePrefix}stale-{Guid.NewGuid():N}.json")
                );
            }
        }
        catch
        { /* a concurrent reaper already claimed it; WriteAtomic overwrites */
        }
    }

    /// <summary>Masters this process still owes a confirmed teardown. Empty
    /// when no journal exists (local-only fleet, xUnit child, or clean drain).</summary>
    public static IReadOnlyList<MasterRecord> SnapshotOwnMasters()
    {
        try
        {
            lock (FileLock)
            {
                return ReadOwnJournal()?.Masters ?? (IReadOnlyList<MasterRecord>)[];
            }
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Count of this process's journaled masters, for composing the
    /// abort backstop deadline.</summary>
    public static int OwnMasterCount() => SnapshotOwnMasters().Count;

    /// <summary>
    /// All journals in the temp dir whose coordinator is dead. Liveness is PID
    /// <b>plus start time</b>: a live PID whose start time doesn't match is a
    /// recycled PID, i.e. the coordinator is dead (PID-only testing would fail
    /// open and the orphan would never be reaped). The same check covers a dead
    /// run's journal squatting this process's own recycled pid — there is no
    /// path-based own-journal exemption. A process we cannot inspect
    /// counts as alive — never reap a live sibling coordinator's masters.
    /// Unparseable journals are deleted (writes are temp-then-rename, so a torn
    /// file cannot exist; anything unparseable is debris).
    /// </summary>
    public static IReadOnlyList<OrphanedJournal> SnapshotOrphanedJournals()
    {
        var orphaned = new List<OrphanedJournal>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Path.GetTempPath(), $"{FilePrefix}*.json");
        }
        catch
        {
            return orphaned;
        }

        foreach (var file in files)
        {
            JournalFile? journal;
            try
            {
                journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(file), Json);
            }
            catch (UnauthorizedAccessException)
            {
                continue; // shared /tmp on Linux: not ours
            }
            catch (IOException)
            {
                continue; // mid-write by a live sibling, or transient
            }
            catch
            {
                journal = null;
            }

            if (journal is null)
            {
                TryDelete(file);
                continue;
            }

            if (IsCoordinatorAlive(journal.CoordinatorPid, journal.CoordinatorStartTimeUtc))
            {
                continue;
            }

            orphaned.Add(new OrphanedJournal(file, journal));
        }

        return orphaned;
    }

    /// <summary>
    /// Deletes a fully-reaped journal, re-checking coordinator identity first:
    /// mid-reap, a live coordinator that recycled the dead one's pid may have
    /// re-claimed the path with its own journal, and a blind path-delete would
    /// destroy that live journal. Only the file still carrying the reaped
    /// coordinator's identity is deleted.
    /// </summary>
    public static void DeleteJournalIfUnchanged(OrphanedJournal orphan)
    {
        try
        {
            var current = JsonSerializer.Deserialize<JournalFile>(
                File.ReadAllText(orphan.FilePath),
                Json
            );
            if (
                current is null
                || current.CoordinatorPid != orphan.Journal.CoordinatorPid
                || current.CoordinatorStartTimeUtc != orphan.Journal.CoordinatorStartTimeUtc
            )
            {
                return;
            }

            File.Delete(orphan.FilePath);
        }
        catch
        { /* already gone, or unreadable — nothing safe to delete */
        }
    }

    /// <summary>
    /// Control paths referenced by ANY journal in the temp dir — own, live
    /// sibling, or dead coordinator. The age-based stale-socket sweep must skip
    /// these: a journaled socket already has exactly one owner (its coordinator
    /// while alive, the orphan reaper once it is dead), and age-sweeping it
    /// would either kill a live sibling's master outright or strip a kept
    /// survivor's only remaining handle.
    /// </summary>
    public static IReadOnlyCollection<string> SnapshotJournaledControlPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Path.GetTempPath(), $"{FilePrefix}*.json");
        }
        catch
        {
            return paths;
        }

        foreach (var file in files)
        {
            try
            {
                var journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(file), Json);
                foreach (var m in journal?.Masters ?? new List<MasterRecord>())
                {
                    if (!string.IsNullOrEmpty(m.ControlPath))
                    {
                        paths.Add(m.ControlPath);
                    }
                }
            }
            catch
            { /* unreadable or torn: no paths to protect */
            }
        }

        return paths;
    }

    private static bool IsCoordinatorAlive(int pid, DateTime startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            return Math.Abs((process.StartTime.ToUniversalTime() - startUtc).TotalSeconds)
                <= StartTimeToleranceSeconds;
        }
        catch (ArgumentException)
        {
            return false; // no such pid
        }
        catch (InvalidOperationException)
        {
            return false; // exited between lookup and read
        }
        catch
        {
            return true; // unreadable (e.g. another user's process): fail safe
        }
    }

    private static JournalFile? ReadOwnJournal()
    {
        try
        {
            if (!File.Exists(OwnJournalPath))
            {
                return null;
            }

            var journal = JsonSerializer.Deserialize<JournalFile>(
                File.ReadAllText(OwnJournalPath),
                Json
            );

            // A file at our pid path can be a dead prior coordinator's leak
            // (recycled pid). Merging into it would inherit its stale start
            // time — a sibling's orphan check would then read THIS live
            // coordinator as dead and reap its masters mid-run. Identity
            // mismatch → treat as absent; the next write overwrites the leak.
            using var self = Process.GetCurrentProcess();
            if (
                journal is null
                || journal.CoordinatorPid != self.Id
                || Math.Abs(
                    (
                        journal.CoordinatorStartTimeUtc - self.StartTime.ToUniversalTime()
                    ).TotalSeconds
                ) > StartTimeToleranceSeconds
            )
            {
                return null;
            }

            return journal;
        }
        catch
        {
            return null;
        }
    }

    private static JournalFile NewOwnJournal()
    {
        using var self = Process.GetCurrentProcess();
        return new JournalFile
        {
            CoordinatorPid = self.Id,
            CoordinatorStartTimeUtc = self.StartTime.ToUniversalTime(),
        };
    }

    private static void WriteAtomic(string path, JournalFile journal)
    {
        // Temp-then-rename so a reader never sees a torn file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        { /* best effort */
        }
    }
}
