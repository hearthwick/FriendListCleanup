using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListCleanup.Friends;

/// <summary>
/// A small "FLC" button in the Social window's title bar, just left of the blue button, shown while the Friend List tab is open.
/// It's drawn on top of the game's interface (an overlay that follows the window), not added to the game's own UI,
/// so it can't break the Social window; it only reads where things are.
/// </summary>
public sealed unsafe class FriendListButton
{
    private const float Gap = 6f;

    private readonly Plugin plugin;
    private bool loggedPlacement;

    public FriendListButton(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        if (!plugin.Configuration.ShowFriendListButton)
            return;

        // Only while the Friend List tab is showing.
        var list = Plugin.GameGui.GetAddonByName<AtkUnitBase>("FriendList");
        if (!TryGetBounds(list, out var min, out var max))
            return;

        var social = Plugin.GameGui.GetAddonByName<AtkUnitBase>("Social");
        if (TryGetBounds(social, out var socialMin, out var socialMax))
        {
            min = Vector2.Min(min, socialMin);
            max = Vector2.Max(max, socialMax);
        }

        var style = ImGui.GetStyle();
        var buttonSize = new Vector2(ImGui.CalcTextSize("FLC").X + style.FramePadding.X * 2, ImGui.GetFrameHeight());

        // Find the blue button in the title bar and sit just left of it, centred on it vertically.
        Vector2 position;
        if (FindTitleBarButton(social, min, max, out var blueMin, out var blueMax) ||
            FindTitleBarButton(list, min, max, out blueMin, out blueMax))
        {
            position = new Vector2(blueMin.X - Gap - buttonSize.X, (blueMin.Y + blueMax.Y - buttonSize.Y) / 2);
            if (!loggedPlacement)
            {
                Plugin.Log.Debug($"FLC button: title bar button found at {blueMin}-{blueMax}; window {min}-{max}.");
                loggedPlacement = true;
            }
        }
        else
        {
            // Couldn't find it: estimate from the window's width (measured from a screenshot: the blue button's left edge
            // is about 14% in from the right, and its middle about 2.5% of the width down from the top).
            var width = max.X - min.X;
            position = new Vector2(max.X - width * 0.1435f - Gap - buttonSize.X, min.Y + width * 0.0245f - buttonSize.Y / 2);
        }

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(ImGuiHelpers.MainViewport.Pos + position);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBackground;

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        {
            if (ImGui.Begin("##FriendListCleanupShortcut", flags))
            {
                if (ImGui.Button("FLC"))
                    plugin.OpenMainUi();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Open Friend List Cleanup\nFriends: {plugin.Configuration.LastKnownFriendCount}/200");
            }

            ImGui.End();
        }
    }

    /// <summary>
    /// Looks for the widest button in the right half of the window's title bar (the blue one; the icons beside it are smaller).
    /// Reads node positions only.
    /// </summary>
    private static bool FindTitleBarButton(AtkUnitBase* addon, Vector2 windowMin, Vector2 windowMax,
                                           out Vector2 foundMin, out Vector2 foundMax)
    {
        foundMin = foundMax = default;
        if (addon == null || !addon->IsVisible)
            return false;

        var windowSize = windowMax - windowMin;
        var titleBarBottom = windowMin.Y + windowSize.Y * 0.06f;
        var bestWidth = 0f;

        var manager = addon->UldManager;
        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node == null || (ushort)node->Type < 1000 || !node->IsVisible())
                continue;

            var scale = addon->Scale * node->ScaleX;
            var nodeMin = new Vector2(node->ScreenX, node->ScreenY);
            var nodeMax = nodeMin + new Vector2(node->Width, node->Height) * scale;
            var centreY = (nodeMin.Y + nodeMax.Y) / 2;
            var width = nodeMax.X - nodeMin.X;

            if (centreY > titleBarBottom || nodeMin.X < windowMin.X + windowSize.X / 2 || width <= bestWidth)
                continue;
            if (width > windowSize.X * 0.25f) // too wide to be a title bar button
                continue;

            bestWidth = width;
            foundMin = nodeMin;
            foundMax = nodeMax;
        }

        return bestWidth > 0;
    }

    /// <summary>Where a game window is on screen, if it's open.</summary>
    private static bool TryGetBounds(AtkUnitBase* addon, out Vector2 min, out Vector2 max)
    {
        min = max = default;
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return false;

        min = new Vector2(addon->X, addon->Y);
        max = min + new Vector2(addon->RootNode->Width, addon->RootNode->Height) * addon->Scale;
        return true;
    }
}
