using System;
using Dalamud.Game.Gui.Dtr;

namespace FriendListCleanup.Friends;

/// <summary>
/// A warning in Dalamud's server info bar (top right of the screen) when your friend list is nearly full.
/// Invisible until you reach the "warn at" count; click it to open Friend List Cleanup.
/// Uses the count saved the last time the game loaded your friend list, so it works before you open the list.
/// </summary>
public sealed class FriendCountWarning : IDisposable
{
    private const int FriendCap = 200;

    private readonly Plugin plugin;
    private IDtrBarEntry? entry;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private int shownCount = -1;

    public FriendCountWarning(Plugin plugin) => this.plugin = plugin;

    /// <summary>Call every frame; does real work at most once a second.</summary>
    public void Update()
    {
        if (DateTime.UtcNow < nextUpdateUtc)
            return;
        nextUpdateUtc = DateTime.UtcNow.AddSeconds(1);

        var cfg = plugin.Configuration;
        var count = cfg.LastKnownFriendCount;
        var show = cfg.ShowServerBarWarning && count > 0 && count >= cfg.WarnAtFriends;

        if (!show)
        {
            if (entry is not null)
                entry.Shown = false;
            shownCount = -1;
            return;
        }

        if (entry is null)
        {
            entry = Plugin.DtrBar.Get("Friend List Cleanup");
            entry.OnClick = _ => plugin.OpenMainUi();
        }

        if (shownCount != count)
        {
            shownCount = count;
            entry.Text = $"Friends {count}/{FriendCap}";
            entry.Tooltip = $"Your friend list is nearly full ({FriendCap - count} slots left).\nClick to open Friend List Cleanup.";
        }

        entry.Shown = true;
    }

    public void Dispose()
    {
        entry?.Remove();
        entry = null;
    }
}
