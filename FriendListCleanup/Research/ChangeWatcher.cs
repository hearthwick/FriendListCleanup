using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FriendListCleanup.Friends;

namespace FriendListCleanup.Research;

/// <summary>
/// RESEARCH TOOL. Snapshots the game's friend list once a second and logs every field that changed since the
/// last snapshot, per friend. Built to work out what the unlabelled per-friend timestamps mean:
/// e.g. if a friend's timestamp jumps to "now" when they log in, it's a last-online time;
/// if it only changes when someone is added, it's the date added.
/// Read-only: it only reads game memory. Off until you press Start.
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    private readonly object gate = new();
    private readonly List<string> lines = new();
    private Dictionary<ulong, FriendDumpRow> last = new();
    private DateTime nextSnapshotUtc = DateTime.MinValue;

    public bool IsRunning { get; private set; }
    public int FriendCount { get; private set; }
    public string[] Lines { get { lock (gate) return lines.ToArray(); } }
    public void Clear() { lock (gate) lines.Clear(); }

    public void Start()
    {
        if (IsRunning)
            return;
        last = new Dictionary<ulong, FriendDumpRow>();
        IsRunning = true;
        Plugin.Framework.Update += OnUpdate;
        Add("Watching started. The first snapshot is the baseline; changes after that are listed here.");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        Plugin.Framework.Update -= OnUpdate;
        IsRunning = false;
        Add("Watching stopped.");
    }

    // Runs on the game's framework thread, every frame; we only do work once a second.
    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextSnapshotUtc)
            return;
        nextSnapshotUtc = DateTime.UtcNow.AddSeconds(1);

        try
        {
            var now = FriendDump.Capture().GroupBy(r => r.ContentId).ToDictionary(g => g.Key, g => g.First());
            FriendCount = now.Count;

            // While the game reloads the list it's briefly empty. Skip those snapshots instead of reporting
            // everyone as gone and then new again.
            if (now.Count == 0)
                return;

            if (last.Count == 0)
            {
                last = now;
                var stamps = now.Values.Where(r => r.RawTimestamp > 0).Select(r => r.RawTimestamp).ToList();
                if (stamps.Count > 0)
                    Add($"Baseline: {now.Count} friends; timestamps from {FriendDump.TimestampAsDate(stamps.Min())} to {FriendDump.TimestampAsDate(stamps.Max())}.");
                return;
            }

            foreach (var (id, row) in now)
            {
                if (!last.TryGetValue(id, out var before))
                {
                    Add($"NEW friend {row.Name}: timestamp {row.RawTimestamp} ({row.TimestampAsDate}), sort {row.Sort}");
                    continue;
                }

                var changes = Diff(before, row);
                if (changes.Count > 0)
                    Add($"{row.Name}: {string.Join("; ", changes)}");
            }

            foreach (var (id, row) in last)
            {
                if (!now.ContainsKey(id))
                    Add($"GONE from list: {row.Name} (had timestamp {row.RawTimestamp}, {row.TimestampAsDate})");
            }

            last = now;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Watch] snapshot failed");
        }
    }

    private static List<string> Diff(FriendDumpRow a, FriendDumpRow b)
    {
        var c = new List<string>();
        void Check(string field, object x, object y)
        {
            if (!Equals(x, y))
                c.Add($"{field} {x} → {y}");
        }

        Check("timestamp", $"{a.RawTimestamp} ({a.TimestampAsDate})", $"{b.RawTimestamp} ({b.TimestampAsDate})");
        Check("sort", a.Sort, b.Sort);
        Check("status", a.Status, b.Status);
        Check("current world", a.CurrentWorld, b.CurrentWorld);
        Check("location", a.Location, b.Location);
        Check("job", a.Job, b.Job);
        Check("FC tag", a.FcTag, b.FcTag);
        Check("group", a.Group, b.Group);
        Check("other server", a.OtherServer, b.OtherServer);
        return c;
    }

    private void Add(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (gate)
        {
            lines.Add(stamped);
            if (lines.Count > 1000)
                lines.RemoveAt(0);
        }
        Plugin.Log.Information($"[Watch] {stamped}");
    }

    public void Dispose() => Stop();
}
