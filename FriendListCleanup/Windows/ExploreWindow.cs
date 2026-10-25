using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FriendListCleanup.Friends;

namespace FriendListCleanup.Windows;

/// <summary>
/// Developer view: every field the game holds for each friend, including ones the normal UI hides.
/// Open with /flc explore. Not meant for regular players; it's how we look for new activity signals.
/// </summary>
public sealed class ExploreWindow : Window
{
    private readonly Plugin plugin;
    private List<FriendDumpRow> rows = new();
    private string? lastExport;
    private DateTime? capturedAt;

    public ExploreWindow(Plugin plugin) : base("Friend List Cleanup: Explore###FriendListCleanupExplore")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##exploreTabs");
        if (!tabs.Success)
            return;

        using (var dataTab = ImRaii.TabItem("Friend data"))
        {
            if (dataTab.Success)
                DrawFriendData();
        }

        using (var watchTab = ImRaii.TabItem("Change watcher"))
        {
            if (watchTab.Success)
                DrawWatcher();
        }

        using (var pagesTab = ImRaii.TabItem("Lodestone pages"))
        {
            if (pagesTab.Success)
                DrawPageSaver();
        }

        using (var logTab = ImRaii.TabItem("Menu logger"))
        {
            if (logTab.Success)
                DrawMenuLogger();
        }
    }

    private string pageFilter = string.Empty;
    private readonly HashSet<ulong> pagePicks = new();
    private DateTime lastPageRefresh = DateTime.MinValue;

    private void DrawPageSaver()
    {
        var saver = plugin.PageSaver;

        // Make sure the friend list is loaded even if the main window hasn't been opened since the plugin loaded.
        if (DateTime.UtcNow - lastPageRefresh > TimeSpan.FromSeconds(2))
        {
            lastPageRefresh = DateTime.UtcNow;
            plugin.RefreshFriendList();
        }

        ImGui.TextWrapped("Research: saves friends' public Lodestone pages (profile, class/job, mounts, minions, achievements) " +
                          "to the plugin's folder, so we can design the profile fingerprint from the real pages. " +
                          "Tick the friends you want, then press Save.");
        ImGui.Spacing();

        using (ImRaii.Disabled(saver.Busy || pagePicks.Count == 0))
        {
            if (ImGui.Button($"Save ticked friends ({pagePicks.Count})"))
            {
                List<(string Id, string Name)> queue;
                lock (plugin.Scanner.Gate)
                {
                    queue = plugin.Scanner.CurrentRecords()
                        .Where(r => pagePicks.Contains(r.ContentId) && r.LodestoneId is not null)
                        .Select(r => (r.LodestoneId!, r.Name))
                        .ToList();
                }
                saver.SaveAll(queue);
                pagePicks.Clear();
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##pageFilter", "Filter by name", ref pageFilter, 40);

        if (saver.Status is { } status)
            ImGui.TextWrapped(status);

        List<(FriendRecord Rec, string Verdict)> friends;
        lock (plugin.Scanner.Gate)
        {
            var cfg = plugin.Configuration;
            friends = plugin.Scanner.CurrentRecords()
                .Where(r => r.LodestoneId is not null)
                .Where(r => pageFilter.Length == 0 || r.Name.Contains(pageFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => (r, Verdicts.VerdictRules.Label(Verdicts.VerdictRules.Evaluate(r, DateTime.UtcNow, cfg).Verdict)))
                .ToList();
        }

        if (friends.Count == 0)
        {
            ImGui.TextDisabled("No scanned friends to show yet. Run a Scan in the main window first.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##pagePicker", 5, flags, ImGui.GetContentRegionAvail());
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("Friend", ImGuiTableColumnFlags.None, 2f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.None, 1.2f);
        ImGui.TableSetupColumn("Achievements", ImGuiTableColumnFlags.None, 1.2f);
        ImGui.TableSetupColumn("Verdict", ImGuiTableColumnFlags.None, 1.4f);
        ImGui.TableHeadersRow();

        foreach (var (rec, verdict) in friends)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var picked = pagePicks.Contains(rec.ContentId);
            if (ImGui.Checkbox($"##pick{rec.ContentId}", ref picked))
            {
                if (picked) pagePicks.Add(rec.ContentId);
                else pagePicks.Remove(rec.ContentId);
            }

            ImGui.TableNextColumn();
            // Clicking the name ticks/unticks too, so you don't have to hit the small box.
            if (ImGui.Selectable($"{rec.Name}##row{rec.ContentId}", picked))
            {
                if (!pagePicks.Remove(rec.ContentId))
                    pagePicks.Add(rec.ContentId);
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(rec.World);
            ImGui.TableNextColumn();
            ImGui.Text(rec.AchievementsPublic ? "Public" : "Hidden");
            ImGui.TableNextColumn();
            ImGui.Text(verdict);
        }
    }

    private void DrawWatcher()
    {
        var watcher = plugin.ChangeWatcher;
        ImGui.TextWrapped("Research: compares your whole friend list every second and lists every field that changes, " +
                          "including the unlabelled timestamp. Leave it running while a friend logs in or out, changes job or zone, " +
                          "or while you add or remove someone. Everything is also written to /xllog with the tag [Watch].");
        ImGui.Spacing();

        if (!watcher.IsRunning)
        {
            if (ImGui.Button("Start watching"))
                watcher.Start();
        }
        else if (ImGui.Button("Stop watching"))
        {
            watcher.Stop();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            watcher.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled(watcher.IsRunning ? $"Watching {watcher.FriendCount} friends" : "Stopped");

        using var child = ImRaii.Child("##watch", ImGui.GetContentRegionAvail(), true);
        if (!child.Success)
            return;
        foreach (var line in watcher.Lines)
            ImGui.TextWrapped(line);
    }

    private void DrawMenuLogger()
    {
        var logger = plugin.MenuLogger;

        ImGui.TextWrapped("Research: records what the game's Friend List does when you right-click a friend, so we can open that same menu from our window. " +
                          "It only listens; it doesn't change anything. Right-click a few friends in the game's Friend List, then press Escape each time. Don't click Remove.");
        ImGui.Spacing();

        if (!logger.IsRunning)
        {
            if (ImGui.Button("Start logging"))
                Plugin.Framework.RunOnFrameworkThread(logger.Start);
        }
        else if (ImGui.Button("Stop logging"))
        {
            Plugin.Framework.RunOnFrameworkThread(logger.Stop);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            logger.Clear();


        using var child = ImRaii.Child("##log", ImGui.GetContentRegionAvail(), true);
        if (!child.Success)
            return;
        foreach (var line in logger.Lines)
            ImGui.TextWrapped(line);
    }

    private void DrawFriendData()
    {
        if (ImGui.Button("Capture friend list"))
            Plugin.Framework.RunOnFrameworkThread(() =>
            {
                rows = FriendDump.Capture();
                capturedAt = DateTime.Now;
            });

        ImGui.SameLine();
        using (ImRaii.Disabled(rows.Count == 0))
        {
            if (ImGui.Button("Export CSV"))
                lastExport = FriendDump.ExportCsv(rows);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(capturedAt is null ? "Nothing captured yet" : $"{rows.Count} friends captured at {capturedAt:h:mm:ss tt}");

        if (lastExport is not null)
            ImGui.TextWrapped($"Saved: {lastExport}");

        if (rows.Count == 0)
        {
            ImGui.TextWrapped("Open your Friend List once (/friendlist), then press Capture.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollX |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;

        using var table = ImRaii.Table("##dump", FriendDump.Columns.Length, flags, ImGui.GetContentRegionAvail());
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(2, 1); // keep # and Name visible while scrolling sideways
        foreach (var c in FriendDump.Columns)
            ImGui.TableSetupColumn(c);
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            Cell(r.Index.ToString());
            Cell(r.ContentId.ToString());
            Cell(r.Name);
            Cell(r.HomeWorld);
            Cell(r.CurrentWorld);
            Cell(r.Status);
            Cell(r.Group);
            Cell(r.OtherServer ? "yes" : "");
            Cell(r.Location);
            Cell(r.GrandCompany);
            Cell(r.ClientLanguage);
            Cell(r.Languages);
            Cell(r.Sex.ToString());
            Cell(r.Job);
            Cell(r.FcTag);
            Cell(r.Sort.ToString());
            Cell(r.RawTimestamp.ToString());
            Cell(r.TimestampAsDate);
        }
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.Text(text);
    }
}
