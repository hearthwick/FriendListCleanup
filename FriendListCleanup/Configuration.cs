using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FriendListCleanup;

/// <summary>
/// Settings and saved scan results. Dalamud stores this as JSON in the plugin's config folder,
/// so results survive a game restart and a second scan only rechecks stale friends.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>A newest achievement older than this many days counts as "likely inactive".</summary>
    public int InactiveAfterDays { get; set; } = 365;

    /// <summary>The current max job level. Raise this when a new expansion lifts the cap.</summary>
    public int LevelCap { get; set; } = 100;

    /// <summary>
    /// The shortest pause between Lodestone requests, in milliseconds. The scanner slows down by itself if the
    /// Lodestone pushes back, but never goes faster than this.
    /// </summary>
    public int RequestDelayMs { get; set; } = 500;

    /// <summary>
    /// A friend whose Lodestone profile hasn't changed at all for this many days (across at least two scans)
    /// is flagged one step more inactive.
    /// </summary>
    public int QuietDaysBeforeFlag { get; set; } = 28;

    /// <summary>Friends checked within this many days are skipped on a normal scan.</summary>
    public int RecheckAfterDays { get; set; } = 7;

    /// <summary>Friends added within this many days are labelled "New friend" and never flagged.</summary>
    public int NewFriendDays { get; set; } = 90;

    /// <summary>Show a warning in the server info bar once you have at least this many friends (the cap is 200).</summary>
    public int WarnAtFriends { get; set; } = 190;

    /// <summary>Show the server info bar warning at all. It only ever appears when you're at or above WarnAtFriends.</summary>
    public bool ShowServerBarWarning { get; set; } = true;

    /// <summary>Show a small button next to the game's Friend List window that opens this plugin.</summary>
    public bool ShowFriendListButton { get; set; } = true;

    /// <summary>
    /// How many friends you had the last time the game's friend list was loaded. The game only loads the list once
    /// you open it, so the warning uses this saved count until then.
    /// </summary>
    public int LastKnownFriendCount { get; set; }

    /// <summary>When the last scan finished (UTC).</summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>Saved Lodestone results, keyed by the friend's content ID.</summary>
    public Dictionary<ulong, FriendRecord> Records { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>Brings settings saved by older versions up to date.</summary>
    public void Upgrade()
    {
        if (Version < 2)
        {
            // v2: pacing became adaptive, so the minimum pause can be shorter.
            if (RequestDelayMs == 1000)
                RequestDelayMs = 500;
            Version = 2;
            Save();
        }

        if (Version < 3)
        {
            // v3: friends now carry a scan version. Friends already fully checked by the fingerprint scanner
            // (before this field existed) count as up to date, so they don't all become due again.
            foreach (var r in Records.Values)
            {
                var hasEverything = r.Fingerprint.Count > 0 && r.TopJobLevel is not null &&
                                    (!r.AchievementsPublic || r.OldestAchievementUtc is not null);
                if (hasEverything)
                    r.ScanVersion = FriendRecord.CurrentScanVersion;
            }

            Version = 3;
            Save();
        }

        if (Version < 4)
        {
            // v4: name search got better (exact-phrase search). Retry friends we couldn't find before.
            foreach (var r in Records.Values)
            {
                if (r.Problem is "Couldn't find them on the Lodestone")
                    r.ScanVersion = 0;
            }

            Version = 4;
            Save();
        }
    }
}
