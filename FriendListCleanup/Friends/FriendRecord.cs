using System;
using System.Collections.Generic;

namespace FriendListCleanup;

/// <summary>
/// One friend: who they are (from the game) plus what the Lodestone told us (from a scan).
/// </summary>
[Serializable]
public class FriendRecord
{
    // --- From the game's friend list ---
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;

    /// <summary>When you became friends (UTC), from the game's own data.</summary>
    public DateTime? FriendedUtc { get; set; }

    /// <summary>Shown in-game as "(Unable to Retrieve)": almost always a deleted character.</summary>
    public bool Unretrievable { get; set; }

    // --- From the Lodestone ---
    /// <summary>The number in the friend's Lodestone URL. Saved so later scans skip the search step.</summary>
    public string? LodestoneId { get; set; }

    /// <summary>False when the friend hides achievements on the Lodestone.</summary>
    public bool AchievementsPublic { get; set; }

    /// <summary>Date of the friend's newest achievement (UTC), when public.</summary>
    public DateTime? NewestAchievementUtc { get; set; }

    /// <summary>Highest level across all jobs, or null if not fetched.</summary>
    public int? TopJobLevel { get; set; }

    /// <summary>How many jobs are at the level cap.</summary>
    public int? JobsAtCap { get; set; }

    /// <summary>The last time the plugin saw this friend online in your game (UTC). Recorded passively, no traffic.</summary>
    public DateTime? LastSeenOnlineUtc { get; set; }

    /// <summary>Where they were when last seen online.</summary>
    public string? LastSeenZone { get; set; }

    /// <summary>Occult Crescent knowledge level (content from patch 7.25, 2025). Null = never played it.</summary>
    public int? OccultLevel { get; set; }

    /// <summary>
    /// Their oldest achievement (UTC), when public. Close to when the character was created; used as a reference point
    /// to estimate other characters' ages from their Lodestone IDs. Fetched once and kept.
    /// </summary>
    public DateTime? OldestAchievementUtc { get; set; }

    /// <summary>Total achievements, when public.</summary>
    public int? AchievementCount { get; set; }

    // --- Profile fingerprint ---
    // A short hash per section of their Lodestone pages (gear, EXP, mounts...). If any section's hash differs
    // between two scans, they played in between. We keep hashes only, not the page contents.

    /// <summary>Section name → hash, from the most recent scan.</summary>
    public Dictionary<string, string> Fingerprint { get; set; } = new();

    /// <summary>When we first saved a fingerprint (the baseline).</summary>
    public DateTime? FingerprintFirstUtc { get; set; }

    /// <summary>When the fingerprint was last refreshed.</summary>
    public DateTime? FingerprintLastUtc { get; set; }

    /// <summary>The scan at which we last saw a change. The activity happened after <see cref="LastChangeWindowStartUtc"/>.</summary>
    public DateTime? LastChangeUtc { get; set; }

    /// <summary>The scan before the change, so we can say "changed between X and Y".</summary>
    public DateTime? LastChangeWindowStartUtc { get; set; }

    /// <summary>Which sections changed last time, e.g. "Gear, Jobs".</summary>
    public string? LastChangeSections { get; set; }

    /// <summary>
    /// Which version of the scanner last checked this friend completely. When a release collects new fields,
    /// <see cref="CurrentScanVersion"/> goes up and everyone checked by an older version becomes due again.
    /// </summary>
    public int ScanVersion { get; set; }

    /// <summary>
    /// Bump this whenever the scanner starts collecting something new.
    ///   1 = achievements + job levels (v0.1)
    ///   2 = profile fingerprint, Occult Crescent, oldest achievement (character age)
    /// </summary>
    public const int CurrentScanVersion = 2;

    /// <summary>When this friend was last checked (UTC). Null = never.</summary>
    public DateTime? CheckedUtc { get; set; }

    /// <summary>Why the last check failed, if it did. Shown as a tooltip.</summary>
    public string? Problem { get; set; }

    public string LodestoneUrl => LodestoneId is null
        ? string.Empty
        : $"https://na.finalfantasyxiv.com/lodestone/character/{LodestoneId}/";
}
