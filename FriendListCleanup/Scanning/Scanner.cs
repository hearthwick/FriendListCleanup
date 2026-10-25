using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FriendListCleanup.Friends;
using FriendListCleanup.Lodestone;

namespace FriendListCleanup.Scanning;

/// <summary>
/// Runs a scan in the background: for each friend, look them up on the Lodestone at a polite pace
/// and save what we find. The window reads progress and results from here every frame.
/// </summary>
public sealed class Scanner : IDisposable
{
    private readonly Configuration config;
    private readonly LodestoneClient lodestone;

    /// <summary>The pause currently used between Lodestone requests (it grows if the Lodestone pushes back).</summary>
    public int CurrentDelayMs => lodestone.CurrentDelayMs;
    private CancellationTokenSource? cts;

    /// <summary>Guards the records, which the scan writes and the window reads at the same time.</summary>
    public readonly object Gate = new();

    /// <summary>Content IDs of everyone on the friend list right now, in game order.</summary>
    private List<ulong> current = new();

    /// <summary>The last read that differed from <see cref="current"/>, waiting to be confirmed.</summary>
    private List<ulong>? pending;

    /// <summary>When <see cref="pending"/> was first seen. A changed list is accepted once it has stayed the same this long.</summary>
    private DateTime pendingSinceUtc;

    /// <summary>How long a changed list must stay unchanged before we trust it (the window reads every 500 ms).</summary>
    private static readonly TimeSpan StableFor = TimeSpan.FromSeconds(1);

    /// <summary>
    /// True while the game seems to be reloading the friend list (two reads in a row disagree).
    /// The window shows "updating" instead of a half-loaded list.
    /// </summary>
    public bool IsUpdating => listChanging || DateTime.UtcNow < holdUntilUtc;

    private bool listChanging;
    private DateTime holdUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Treat the list as "updating" for a moment, e.g. right after a removal, before the game has started reloading.
    /// It stays locked until then AND until two reads in a row agree.
    /// </summary>
    public void HoldUpdates(TimeSpan duration) => holdUntilUtc = DateTime.UtcNow + duration;

    public bool IsRunning { get; private set; }
    public int Total { get; private set; }
    public int Done { get; private set; }
    public string? CurrentName { get; private set; }

    public Scanner(Configuration config)
    {
        this.config = config;
        lodestone = new LodestoneClient(config);
    }

    /// <summary>
    /// Updates who is on the friend list. New friends get a blank record; renamed or transferred
    /// friends keep their record (it's keyed by content ID) with the new name and world.
    /// </summary>
    public void SetFriends(List<FriendEntry> friends)
    {
        lock (Gate)
        {
            var ids = friends.Select(f => f.ContentId).ToList();

            // After a removal the game clears and refills the list. A changed list is only accepted once it has
            // stayed exactly the same for StableFor, so we never show (or let you click) a half-loaded list.
            if (current.Count > 0 && !ids.SequenceEqual(current))
            {
                if (ids.Count == 0 || pending is null || !ids.SequenceEqual(pending))
                {
                    pending = ids;
                    pendingSinceUtc = DateTime.UtcNow;
                    listChanging = true;
                    return;
                }

                if (DateTime.UtcNow - pendingSinceUtc < StableFor)
                {
                    listChanging = true;
                    return;
                }
            }

            pending = null;
            listChanging = false;

            foreach (var f in friends)
            {
                if (!config.Records.TryGetValue(f.ContentId, out var rec))
                {
                    rec = new FriendRecord { ContentId = f.ContentId };
                    config.Records[f.ContentId] = rec;
                }
                else if (rec.Name != f.Name || rec.World != f.World)
                {
                    // Name or world changed: the old Lodestone ID is still right, but re-check the data.
                    rec.CheckedUtc = null;
                }

                rec.Name = f.Name;
                rec.World = f.World;
                rec.Unretrievable = f.Unretrievable;
                rec.FriendedUtc = f.FriendedUtc;

                if (f.Online)
                {
                    // Seen online right now: the most direct activity signal there is.
                    rec.LastSeenOnlineUtc = DateTime.UtcNow;
                    if (f.Zone.Length > 0)
                        rec.LastSeenZone = f.Zone;
                }
            }

            current = ids;

            // Remember the count for the friend count warning, which needs it before the game loads the list next session.
            if (ids.Count > 0 && config.LastKnownFriendCount != ids.Count)
            {
                config.LastKnownFriendCount = ids.Count;
                config.Save();
            }
        }
    }

    /// <summary>Friends currently on the list, for the window. Call inside lock (Gate).</summary>
    public IEnumerable<FriendRecord> CurrentRecords()
        => current.Where(config.Records.ContainsKey).Select(id => config.Records[id]);

    public int FriendCount { get { lock (Gate) return current.Count; } }

    /// <summary>Starts a scan. With recheckAll = false, friends checked recently are skipped.</summary>
    public void Start(bool recheckAll)
    {
        if (IsRunning)
            return;

        List<FriendRecord> todo;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            todo = CurrentRecords()
                .Where(r => !r.Unretrievable) // nothing to look up: the character is gone
                .Where(r => recheckAll || Verdicts.VerdictRules.IsDue(r, now, config))
                .ToList();
        }

        Total = todo.Count;
        Done = 0;
        if (Total == 0)
            return;

        cts = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(() => RunAsync(todo, cts.Token));
    }

    public void Stop() => cts?.Cancel();

    private async Task RunAsync(List<FriendRecord> todo, CancellationToken ct)
    {
        try
        {
            foreach (var rec in todo)
            {
                ct.ThrowIfCancellationRequested();
                string name, world; string? knownId;
                lock (Gate) { name = rec.Name; world = rec.World; knownId = rec.LodestoneId; }
                CurrentName = name;

                await CheckOneAsync(rec, name, world, knownId, ct);

                Done++;
                if (Done % 10 == 0)
                    SaveOnFrameworkThread();
            }

            config.LastScanUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            Plugin.Log.Information("Scan stopped by the player.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Scan failed.");
        }
        finally
        {
            IsRunning = false;
            CurrentName = null;
            SaveOnFrameworkThread();
        }
    }

    /// <summary>
    /// Reads one friend's public Lodestone pages (profile, class/job, mounts, minions, achievements), builds their
    /// fingerprint and compares it with the last one. About 5 requests per friend (6 the first time, to find their page).
    /// </summary>
    private async Task CheckOneAsync(FriendRecord rec, string name, string world, string? knownId, CancellationToken ct)
    {
        string? id = knownId;
        string? problem = null;
        var sections = new Dictionary<string, string>();
        bool? achievementsPublic = null;
        DateTime? newestAchievement = null;
        int? achievementCount = null;
        DateTime? oldestAchievement = null;
        bool needOldest;
        lock (Gate)
            needOldest = rec.OldestAchievementUtc is null;
        (int Top, int AtCap, int? Occult)? levels = null;

        try
        {
            id ??= await lodestone.FindCharacterIdAsync(name, world, ct);

            if (id is null)
            {
                problem = "Couldn't find them on the Lodestone";
            }
            else
            {
                var (status, html) = await lodestone.GetPageAsync(id, "", ct);
                if (status == HttpStatusCode.NotFound)
                {
                    // Their page is gone (deleted or renamed since we saved the ID). Search again next time.
                    id = null;
                    problem = "Lodestone page not found";
                }
                else
                {
                    if (html is not null)
                        ProfileParser.ParseProfile(html, sections);

                    (_, html) = await lodestone.GetPageAsync(id, "class_job/", ct);
                    if (html is not null)
                        levels = ProfileParser.ParseClassJob(html, config.LevelCap, sections);

                    // A character with no mounts (or no minions) has no page at all: the Lodestone answers "not found".
                    // That means 0, not a failure. (Seen 24 Oct on a level 28 character with no minions.)
                    var (mountStatus, mounts) = await lodestone.GetPageAsync(id, "mount/", ct);
                    var (minionStatus, minions) = await lodestone.GetPageAsync(id, "minion/", ct);
                    var mountText = mountStatus == HttpStatusCode.NotFound ? "0" : mounts is null ? null : ProfileParser.ParseCollection(mounts, "mount");
                    var minionText = minionStatus == HttpStatusCode.NotFound ? "0" : minions is null ? null : ProfileParser.ParseCollection(minions, "minion");
                    if (mountText is not null && minionText is not null)
                        sections["Collections"] = $"mounts {mountText} · minions {minionText}";

                    (status, html) = await lodestone.GetPageAsync(id, "achievement/", ct);
                    if (status == HttpStatusCode.Forbidden)
                    {
                        achievementsPublic = false;
                    }
                    else if (html is not null)
                    {
                        achievementsPublic = true;
                        var (count, points, newest) = ProfileParser.ParseAchievements(html);
                        achievementCount = count;
                        newestAchievement = newest;
                        sections["Achievements"] = $"{count} · {points} pts · newest {newest:yyyy-MM-dd}";

                        // Once per friend: their oldest achievement, which is close to when the character was created.
                        // Sorted oldest-first, the first entry is the one we want.
                        if (needOldest)
                        {
                            var (_, oldestPage) = await lodestone.GetPageAsync(id, "achievement/?order=2", ct);
                            if (oldestPage is not null)
                                oldestAchievement = ProfileParser.ParseAchievements(oldestPage).Newest; // "first entry" on this page = oldest
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problem = "Lodestone request failed";
            Plugin.Log.Warning(ex, $"Lookup failed for {name} ({world}).");
        }

        var now = DateTime.UtcNow;
        lock (Gate)
        {
            rec.LodestoneId = id;
            rec.Problem = problem;
            rec.CheckedUtc = now;

            // Only a complete check counts as up to date; otherwise this friend stays due for the next Scan.
            var complete = problem is null && id is not null && achievementsPublic is not null && levels is not null
                           && sections.ContainsKey("Look") && sections.ContainsKey("Collections");

            // "Not on the Lodestone" is a finished answer too (we searched properly), so they wait for the normal
            // weekly recheck instead of coming up on every Scan. A failed request is not: that stays due.
            var notFound = problem is "Couldn't find them on the Lodestone" or "Lodestone page not found";
            complete |= notFound;
            rec.ScanVersion = complete ? FriendRecord.CurrentScanVersion : Math.Min(rec.ScanVersion, FriendRecord.CurrentScanVersion - 1);

            if (achievementsPublic is { } isPublic)
            {
                rec.AchievementsPublic = isPublic;
                rec.NewestAchievementUtc = isPublic ? newestAchievement : null;
                rec.AchievementCount = isPublic ? achievementCount : null;
                if (oldestAchievement is not null)
                    rec.OldestAchievementUtc = oldestAchievement;
            }

            if (levels is { } lv)
            {
                rec.TopJobLevel = lv.Top;
                rec.JobsAtCap = lv.AtCap;
                rec.OccultLevel = lv.Occult;
            }

            UpdateFingerprint(rec, sections, now);
        }
    }

    /// <summary>
    /// Compares this scan's sections with the saved fingerprint. Sections we couldn't read this time are left as they
    /// were, so a failed page never looks like a change.
    /// </summary>
    private static void UpdateFingerprint(FriendRecord rec, Dictionary<string, string> sections, DateTime now)
    {
        if (sections.Count == 0)
            return;

        var hashes = sections.ToDictionary(kv => kv.Key, kv => ProfileParser.ShortHash(kv.Value));

        if (rec.Fingerprint.Count > 0)
        {
            var changed = ProfileParser.SectionOrder
                .Where(k => hashes.TryGetValue(k, out var h) && rec.Fingerprint.TryGetValue(k, out var old) && old != h)
                .ToList();

            if (changed.Count > 0)
            {
                rec.LastChangeWindowStartUtc = rec.FingerprintLastUtc;
                rec.LastChangeUtc = now;
                rec.LastChangeSections = string.Join(", ", changed);
                Plugin.Log.Information($"{rec.Name}: profile changed since {rec.FingerprintLastUtc:yyyy-MM-dd} ({rec.LastChangeSections}).");
            }
        }

        foreach (var (key, hash) in hashes)
            rec.Fingerprint[key] = hash;

        rec.FingerprintFirstUtc ??= now;
        rec.FingerprintLastUtc = now;
    }

    private void SaveOnFrameworkThread()
        => Plugin.Framework.RunOnFrameworkThread(() => { lock (Gate) config.Save(); });

    public void Dispose()
    {
        cts?.Cancel();
        lodestone.Dispose();
    }
}
