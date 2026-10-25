using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FriendListCleanup.Verdicts;

namespace FriendListCleanup.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    /// <summary>Which verdict the table is filtered to, or null for everyone.</summary>
    private Verdict? filter;

    /// <summary>When we last re-read the friend list, so removals made in-game show up here within seconds.</summary>
    private DateTime lastRefresh = DateTime.MinValue;

    /// <summary>Friend count when the window opened, to show how many slots you've freed.</summary>
    private int? startCount;

    /// <summary>A frozen copy of one friend for this frame, so the scan can keep writing safely.</summary>
    private readonly record struct Row(
        ulong ContentId, string Name, string World, string LodestoneUrl,
        int? TopJobLevel, int? JobsAtCap, DateTime? NewestUtc, bool AchievementsPublic, bool Checked,
        DateTime? FriendedUtc, DateTime? LastSeenOnlineUtc, string? LastSeenZone, DateTime? LastChangeUtc, DateTime? FingerprintFirstUtc, string? LastChangeSections,
        Verdict Verdict, string Reason);

    private static readonly Verdict[] SummaryOrder =
        [Verdict.Gone, Verdict.LikelyInactive, Verdict.PossiblyInactive, Verdict.CantTell, Verdict.NewFriend, Verdict.Active, Verdict.NotChecked];

    // "###" keeps the window's ID fixed even if the visible title changes.
    public MainWindow(Plugin plugin) : base("Friend List Cleanup###FriendListCleanupMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>
    /// Part of the table's ID. Changing it gives ImGui a "new" table, which starts in the default order
    /// (Verdict: best removal candidates first) instead of whatever column was clicked last.
    /// </summary>
    private int tableGeneration;
    private bool wasScanning;

    /// <summary>Back to the default view: everyone, sorted best removal candidates first.</summary>
    private void ResetView()
    {
        filter = null;
        tableGeneration++;
    }

    public override void OnOpen()
    {
        startCount = null;
        ResetView();
    }

    public override void Draw()
    {
        // Re-read the friend list twice a second (memory only, no server traffic; a few microseconds per read).
        if (DateTime.UtcNow - lastRefresh > TimeSpan.FromMilliseconds(500))
        {
            lastRefresh = DateTime.UtcNow;
            plugin.RefreshFriendList();
        }

        // When a scan finishes, show the fresh results in the default order.
        if (wasScanning && !plugin.Scanner.IsRunning)
            ResetView();
        wasScanning = plugin.Scanner.IsRunning;

        DrawToolbar();

        var rows = BuildRows();
        if (rows.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("No friends loaded yet. Open your Friend List once (type /friendlist), then press Scan.");
            return;
        }

        startCount ??= rows.Count;
        var freed = startCount.Value - rows.Count;
        if (freed > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, $"{freed} removed this session · {rows.Count}/200");
        }

        // While the game reloads its friend list, positions shift; clicking a row could target the wrong slot.
        // So the list is greyed out and can't be clicked until the reload is done (and while a removal is in progress).
        var locked = plugin.Scanner.IsUpdating || plugin.Remover.Busy;
        if (locked)
        {
            var dots = new string('.', 1 + (int)(DateTime.UtcNow.TimeOfDay.TotalSeconds * 2 % 3));
            ImGui.TextColored(ImGuiColors.DalamudYellow, plugin.Remover.Busy
                ? $"Removing{dots} please wait"
                : $"Game is updating your friend list{dots} list locked until it's done");
        }

        if (plugin.MenuMessage is { } message)
            ImGui.TextColored(ImGuiColors.DalamudYellow, message);

        if (plugin.Remover.Status is { } removeStatus)
            ImGui.TextColored(plugin.Remover.StatusIsError ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen, removeStatus);

        ImGui.Spacing();
        using var lockList = ImRaii.Disabled(locked);
        DrawSummary(rows);
        ImGui.Spacing();
        DrawTable(rows);
    }

    private void DrawToolbar()
    {
        var scanner = plugin.Scanner;

        if (scanner.IsRunning)
        {
            if (ImGui.Button("Stop"))
                scanner.Stop();

            ImGui.SameLine();
            var fraction = scanner.Total == 0 ? 0f : (float)scanner.Done / scanner.Total;
            // About 5 page requests per friend, at the current (adaptive) pace.
            var secondsLeft = (scanner.Total - scanner.Done) * 5 * scanner.CurrentDelayMs / 1000;
            // ImGui places the bar's own text just after the filled part, so we draw the bar empty
            // and centre our text on it ourselves.
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), "");
            var text = $"{scanner.Done} / {scanner.Total}  ·  about {Math.Max(1, secondsLeft / 60)} min left  ·  {scanner.CurrentName}";
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var size = ImGui.CalcTextSize(text);
            var pos = new Vector2(
                MathF.Max(min.X, min.X + (max.X - min.X - size.X) / 2),
                min.Y + (max.Y - min.Y - size.Y) / 2);
            ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(ImGuiCol.Text), text);
            return;
        }

        int due;
        lock (scanner.Gate)
            due = scanner.CurrentRecords().Count(r => VerdictRules.IsDue(r, DateTime.UtcNow, plugin.Configuration));
        if (ImGui.Button(due > 0 ? $"Scan ({due} due)###scan" : "Scan###scan"))
            plugin.RefreshFriendList(thenScan: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Checks friends not looked up in the last {plugin.Configuration.RecheckAfterDays} days.");

        ImGui.SameLine();
        if (ImGui.Button("Rescan everyone"))
            plugin.RefreshFriendList(thenScan: true, recheckAll: true);

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        ImGui.TextDisabled("Tip: click a verdict for actions; Ctrl+click to remove.");

        ImGui.SameLine();
        var last = plugin.Configuration.LastScanUtc;
        ImGui.TextDisabled(last is null ? "Never scanned" : $"Last scan {last.Value.ToLocalTime():MMM d, h:mm tt}");
    }

    private List<Row> BuildRows()
    {
        var cfg = plugin.Configuration;
        var now = DateTime.UtcNow;

        lock (plugin.Scanner.Gate)
        {
            // Reference points for estimating character ages from Lodestone IDs (cheap: one pass over the list).
            var ages = CreationEstimator.Build(plugin.Scanner.CurrentRecords());
            return plugin.Scanner.CurrentRecords()
                .Select(r =>
                {
                    var (verdict, reason) = VerdictRules.Evaluate(r, now, cfg, ages);
                    return new Row(r.ContentId, r.Name, r.World, r.LodestoneUrl,
                        r.TopJobLevel, r.JobsAtCap, r.NewestAchievementUtc, r.AchievementsPublic, r.CheckedUtc is not null,
                        r.FriendedUtc, r.LastSeenOnlineUtc, r.LastSeenZone, r.LastChangeUtc, r.FingerprintFirstUtc, r.LastChangeSections, verdict, reason);
                })
                // Best removal candidates first, then oldest achievement first, then by name.
                .OrderBy(r => r.Verdict)
                .ThenBy(r => r.NewestUtc ?? DateTime.MaxValue)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void DrawSummary(List<Row> rows)
    {
        var counts = rows.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count());

        if (ImGui.Button($"All ({rows.Count})"))
            filter = null;

        foreach (var v in SummaryOrder)
        {
            if (!counts.TryGetValue(v, out var n))
                continue;

            ImGui.SameLine();
            // Highlight the button for the filter that's active.
            using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudGrey3, filter == v))
            {
                if (ImGui.Button($"{VerdictRules.Label(v)} ({n})"))
                    filter = filter == v ? null : v;
            }
        }
    }

    private void DrawTable(List<Row> rows)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                                      ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                                      ImGuiTableFlags.NoSavedSettings;

        using var table = ImRaii.Table($"##friends{tableGeneration}", 9, flags, ImGui.GetContentRegionAvail());
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1); // keep the header visible while scrolling
        ImGui.TableSetupColumn("Friend", ImGuiTableColumnFlags.None, 2.2f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.None, 1.2f);
        ImGui.TableSetupColumn("Top job", ImGuiTableColumnFlags.None, 0.8f);
        ImGui.TableSetupColumn("Jobs at cap", ImGuiTableColumnFlags.None, 0.9f);
        ImGui.TableSetupColumn("Newest achievement", ImGuiTableColumnFlags.None, 1.5f);
        ImGui.TableSetupColumn("Friends since", ImGuiTableColumnFlags.None, 1.3f);
        ImGui.TableSetupColumn("Seen online", ImGuiTableColumnFlags.None, 1.2f);
        ImGui.TableSetupColumn("Profile change", ImGuiTableColumnFlags.None, 1.4f);
        ImGui.TableSetupColumn("Verdict", ImGuiTableColumnFlags.DefaultSort, 1.6f);
        ImGui.TableHeadersRow();

        // Click a column header to sort by it; click again to reverse.
        rows = SortRows(rows);

        foreach (var row in rows)
        {
            if (filter is not null && row.Verdict != filter)
                continue;

            ImGui.TableNextRow();

            // Friend name: click to open their Lodestone page in your browser.
            ImGui.TableNextColumn();
            if (row.LodestoneUrl.Length > 0)
            {
                if (ImGui.Selectable($"{row.Name}##{row.ContentId}"))
                    Util.OpenLink(row.LodestoneUrl);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Open their Lodestone page");
            }
            else
            {
                ImGui.Text(row.Name);
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.World);

            ImGui.TableNextColumn();
            ImGui.Text(row.TopJobLevel?.ToString() ?? "–");

            ImGui.TableNextColumn();
            ImGui.Text(row.JobsAtCap?.ToString() ?? "–");

            ImGui.TableNextColumn();
            if (row.NewestUtc is { } newest)
                ImGui.Text(newest.ToLocalTime().ToString("MMM d, yyyy"));
            else if (row.Checked && !row.AchievementsPublic)
                ImGui.TextDisabled("Hidden");
            else
                ImGui.TextDisabled("–");

            ImGui.TableNextColumn();
            if (row.FriendedUtc is { } friended)
                ImGui.Text(friended.ToLocalTime().ToString("MMM d, yyyy"));
            else
                ImGui.TextDisabled("–");

            // Seen online: the last time the plugin noticed them online in your game.
            ImGui.TableNextColumn();
            if (row.LastSeenOnlineUtc is { } seenAt)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, seenAt.ToLocalTime().ToString("MMM d, yyyy"));
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(row.LastSeenZone))
                    ImGui.SetTooltip($"Last seen in {row.LastSeenZone}");
            }
            else
            {
                ImGui.TextDisabled("–");
            }

            // Profile change: when we last saw their Lodestone profile change between two scans.
            ImGui.TableNextColumn();
            if (row.LastChangeUtc is { } changedAt)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, changedAt.ToLocalTime().ToString("MMM d, yyyy"));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Changed: {row.LastChangeSections}");
            }
            else if (row.FingerprintFirstUtc is { } baseline)
            {
                ImGui.TextDisabled($"none since {baseline.ToLocalTime():MMM d}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No changes seen yet. Each scan compares their gear, EXP, mounts, minions and more with the last one.");
            }
            else
            {
                ImGui.TextDisabled("–");
            }

            // Verdict: hover for the reason; click for actions; Ctrl+click to remove straight away.
            ImGui.TableNextColumn();
            DrawVerdictCell(row);
        }
    }

    /// <summary>Applies the column the player clicked in the table header (default: Verdict, best candidates first).</summary>
    private static List<Row> SortRows(List<Row> rows)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount == 0)
            return rows;

        var spec = specs.Specs;
        var ascending = spec.SortDirection != ImGuiSortDirection.Descending;
        IOrderedEnumerable<Row> sorted = spec.ColumnIndex switch
        {
            0 => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            1 => rows.OrderBy(r => r.World, StringComparer.OrdinalIgnoreCase),
            2 => rows.OrderBy(r => r.TopJobLevel ?? -1),
            3 => rows.OrderBy(r => r.JobsAtCap ?? -1),
            4 => rows.OrderBy(r => r.NewestUtc ?? DateTime.MaxValue),
            5 => rows.OrderBy(r => r.FriendedUtc ?? DateTime.MaxValue),
            6 => rows.OrderBy(r => r.LastSeenOnlineUtc ?? DateTime.MinValue),
            7 => rows.OrderBy(r => r.LastChangeUtc ?? DateTime.MinValue),
            // Default: best removal candidates first. Within a verdict: oldest achievement first,
            // then the friends you added longest ago.
            _ => rows.OrderBy(r => r.Verdict).ThenBy(r => r.NewestUtc ?? DateTime.MaxValue).ThenBy(r => r.FriendedUtc ?? DateTime.MaxValue),
        };

        var result = sorted.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!ascending)
            result.Reverse();
        return result;
    }

    /// <summary>Which friend's action menu is asking "are you sure?", if any.</summary>
    private ulong? confirmingRemove;

    private void DrawVerdictCell(Row row)
    {
        var popupId = $"actions##{row.ContentId}";
        var busy = plugin.Remover.Busy;

        using (ImRaii.PushColor(ImGuiCol.Text, VerdictColour(row.Verdict)))
        {
            if (ImGui.Selectable($"{VerdictRules.Label(row.Verdict)}##verdict{row.ContentId}") && !busy)
            {
                if (ImGui.GetIO().KeyCtrl)
                {
                    // Ctrl is the safety catch: a plain click can never remove anyone.
                    plugin.Remover.Remove(row.ContentId, row.Name);
                }
                else
                {
                    confirmingRemove = null;
                    ImGui.OpenPopup(popupId);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            var since = row.FriendedUtc is { } f ? $"\nFriends since {f.ToLocalTime():MMM d, yyyy}" : "";
            ImGui.SetTooltip($"{row.Reason}{since}\n\n" +
                             "Click: actions (remove, Lodestone page)\n" +
                             "Ctrl+click: remove from your friend list right away, no confirmation");
        }

        using var popup = ImRaii.Popup(popupId);
        if (!popup.Success)
            return;

        ImGui.TextDisabled(row.Name);
        ImGui.Separator();

        if (confirmingRemove == row.ContentId)
        {
            ImGui.Text($"Remove {row.Name} from your friend list?");
            if (ImGui.Button("Yes, remove"))
            {
                plugin.Remover.Remove(row.ContentId, row.Name);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("No"))
                confirmingRemove = null;
            return;
        }

        using (ImRaii.Disabled(busy))
        {
            // A button (not a menu item) so the menu stays open for the "are you sure?" step.
            if (ImGui.Button("Remove from Friend List…"))
                confirmingRemove = row.ContentId;
        }

        if (row.LodestoneUrl.Length > 0 && ImGui.Selectable("Open Lodestone page"))
            Util.OpenLink(row.LodestoneUrl);

        if (ImGui.Selectable("Show the game's menu instead"))
            plugin.OpenFriendMenu(row.ContentId, row.Name);
    }

    private static Vector4 VerdictColour(Verdict v) => v switch
    {
        Verdict.Gone or Verdict.LikelyInactive => ImGuiColors.DalamudRed,
        Verdict.PossiblyInactive => ImGuiColors.DalamudOrange,
        Verdict.Active => ImGuiColors.HealerGreen,
        Verdict.NewFriend => ImGuiColors.TankBlue,
        _ => ImGuiColors.DalamudGrey,
    };
}
