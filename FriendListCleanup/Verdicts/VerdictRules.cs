using System;

namespace FriendListCleanup.Verdicts;

/// <summary>Ordered so that sorting by verdict puts the best removal candidates first.</summary>
public enum Verdict
{
    Gone = 0,
    LikelyInactive = 1,
    PossiblyInactive = 2,
    CantTell = 3,
    NewFriend = 4,
    Active = 5,
    NotChecked = 6,
}

/// <summary>
/// Decides how likely a friend is to have stopped playing. Checked in order; the first rule that matches wins.
/// Pure logic with no game or web access, so it's easy to test and reason about.
///
///   1. Gone: the game shows them as "(Unable to Retrieve)". Added recently: New friend. Seen online recently: Active.
///   2. Their Lodestone profile changed between two scans → Active (the strongest evidence we have).
///   3. Public achievements: newest within the cutoff → Active, older → Likely inactive.
///   4. Hidden achievements: no job at the level cap → Possibly inactive, otherwise Can't tell.
///   Then, for 3 and 4: a profile that hasn't changed at all for a long stretch moves one step more inactive,
///   and a friend with only a baseline gets a "recheck after" date.
/// </summary>
public static class VerdictRules
{
    public static (Verdict Verdict, string Reason) Evaluate(FriendRecord r, DateTime nowUtc, Configuration cfg, CreationEstimator? ages = null)
    {
        if (r.Unretrievable)
            return (Verdict.Gone, "Shows as \"Unable to Retrieve\" in-game: almost always a deleted character");

        // Recently added friends are never flagged: too new to judge.
        if (r.FriendedUtc is { } friended && (nowUtc - friended).TotalDays < cfg.NewFriendDays)
            return (Verdict.NewFriend, $"Added {Day(friended)}: too new to judge" + AgeNote(r, ages));

        if (r.CheckedUtc is null)
            return (Verdict.NotChecked, "Not scanned yet");

        if (r.LodestoneId is null)
            return (Verdict.CantTell, r.Problem ?? "Couldn't find them on the Lodestone");

        // Seen online in your game recently: they're playing.
        if (r.LastSeenOnlineUtc is { } seen && (nowUtc - seen).TotalDays <= cfg.InactiveAfterDays)
        {
            var where = string.IsNullOrEmpty(r.LastSeenZone) ? "" : $" in {r.LastSeenZone}";
            return (Verdict.Active, $"Seen online {Day(seen)}{where}");
        }

        // 2. Seen changing between scans: they played in that window.
        if (r.LastChangeUtc is { } changed && (nowUtc - changed).TotalDays <= cfg.InactiveAfterDays)
        {
            var from = r.LastChangeWindowStartUtc is { } start ? $" between {Day(start)} and {Day(changed)}" : $" by {Day(changed)}";
            return (Verdict.Active, $"Profile changed{from} ({r.LastChangeSections})");
        }

        Verdict verdict;
        string reason;

        if (r.NewestAchievementUtc is { } newest)
        {
            var days = (int)(nowUtc - newest).TotalDays;
            if (days <= cfg.InactiveAfterDays)
                return (Verdict.Active, $"Achievement {days} days ago");
            (verdict, reason) = (Verdict.LikelyInactive, $"Newest achievement {days} days ago");
        }
        else if (r.TopJobLevel is null)
        {
            (verdict, reason) = (Verdict.CantTell, r.Problem ?? "Job levels didn't load");
        }
        else if (r.TopJobLevel < cfg.LevelCap)
        {
            (verdict, reason) = (Verdict.PossiblyInactive,
                r.AchievementsPublic ? $"No achievements listed, no job at {cfg.LevelCap}" : $"Achievements hidden, no job at {cfg.LevelCap}");
        }
        else
        {
            (verdict, reason) = (Verdict.CantTell, r.AchievementsPublic ? "No achievements listed" : "Achievements hidden");
        }

        // Extra context from the game data we read.
        reason += AgeNote(r, ages);
        if (r.OccultLevel is > 0)
            reason += $" · played Occult Crescent (2025+, knowledge {r.OccultLevel})";

        // How long has the profile been completely unchanged?
        if (r.FingerprintFirstUtc is { } first && r.FingerprintLastUtc is { } last)
        {
            var quietSince = r.LastChangeUtc ?? first;
            var quietDays = (int)(last - quietSince).TotalDays;

            if (quietDays >= cfg.QuietDaysBeforeFlag)
            {
                reason += $" · profile unchanged for {quietDays} days";
                verdict = verdict switch
                {
                    Verdict.CantTell => Verdict.PossiblyInactive,
                    Verdict.PossiblyInactive => Verdict.LikelyInactive,
                    _ => verdict,
                };
            }
            else if (last - first < TimeSpan.FromDays(1) && verdict is Verdict.CantTell or Verdict.PossiblyInactive)
            {
                // Only one snapshot so far: tell the player when a second one becomes useful.
                reason += $" · baseline saved {Day(first)}, recheck after {Day(first.AddDays(cfg.RecheckAfterDays))}";
            }
            else
            {
                reason += $" · profile unchanged since {Day(quietSince)}";
            }
        }

        return (verdict, reason);
    }

    /// <summary>
    /// True when a friend should be scanned again: never checked, checked by an older version of the scanner
    /// (so they're missing newer fields), or not checked for RecheckAfterDays. Used by both Scan and its "due" count.
    /// </summary>
    public static bool IsDue(FriendRecord r, DateTime nowUtc, Configuration cfg)
        => !r.Unretrievable &&
           (r.CheckedUtc is null ||
            r.ScanVersion < FriendRecord.CurrentScanVersion ||
            (nowUtc - r.CheckedUtc.Value).TotalDays >= cfg.RecheckAfterDays);

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("MMM d");

    private static string AgeNote(FriendRecord r, CreationEstimator? ages)
        => ages?.Describe(r) is { } age ? $" · {age}" : string.Empty;

    public static string Label(Verdict v) => v switch
    {
        Verdict.Gone => "Character likely deleted",
        Verdict.LikelyInactive => "Likely inactive",
        Verdict.PossiblyInactive => "Possibly inactive",
        Verdict.CantTell => "Can't tell",
        Verdict.NewFriend => "New friend",
        Verdict.Active => "Active",
        _ => "Not checked",
    };
}
