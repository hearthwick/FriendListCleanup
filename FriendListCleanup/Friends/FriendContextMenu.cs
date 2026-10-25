using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Utility;
using FriendListCleanup.Verdicts;

namespace FriendListCleanup.Friends;

/// <summary>
/// Adds our verdict to the game's own right-click menu in the Friend List, in the same menu as "Remove from Friend List".
/// Uses Dalamud's context menu service; nothing is sent to the game servers.
/// Clicking the line opens the friend's Lodestone page so you can double-check before removing.
/// </summary>
public sealed class FriendContextMenu : IDisposable
{
    private readonly Plugin plugin;

    public FriendContextMenu(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // Only the game's Friend List window; other menus (party, chat, etc.) are left alone.
        if (args.AddonName != "FriendList" || args.Target is not MenuTargetDefault target)
            return;

        var record = FindRecord(target.TargetContentId, target.TargetName);
        string text;
        string? url = null;

        if (record is null)
        {
            text = "Cleanup: not scanned yet";
        }
        else
        {
            var cfg = plugin.Configuration;
            (Verdict verdict, string reason) result;
            lock (plugin.Scanner.Gate)
            {
                result = VerdictRules.Evaluate(record, DateTime.UtcNow, cfg);
                url = record.LodestoneUrl.Length > 0 ? record.LodestoneUrl : null;
            }

            text = record.Starred
                ? $"Cleanup: Starred, protected ({VerdictRules.Label(result.verdict)})"
                : $"Cleanup: {VerdictRules.Label(result.verdict)} ({result.reason})";
        }

        args.AddMenuItem(new MenuItem
        {
            Name = text,
            UseDefaultPrefix = true, // the small Dalamud icon, so it's clear this line comes from a plugin
            IsEnabled = url is not null,
            OnClicked = _ =>
            {
                if (url is not null)
                    Util.OpenLink(url);
            },
        });
    }

    /// <summary>Matches by content ID first; falls back to the name (needed for "Unable to Retrieve" entries).</summary>
    private FriendRecord? FindRecord(ulong contentId, string name)
    {
        lock (plugin.Scanner.Gate)
        {
            if (contentId != 0 && plugin.Configuration.Records.TryGetValue(contentId, out var byId))
                return byId;

            foreach (var r in plugin.Scanner.CurrentRecords())
            {
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
        }

        return null;
    }

    public void Dispose() => Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
}
