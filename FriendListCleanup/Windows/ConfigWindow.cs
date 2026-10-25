using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FriendListCleanup.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;

    public ConfigWindow(Plugin plugin) : base("Friend List Cleanup Settings###FriendListCleanupConfig")
    {
        config = plugin.Configuration;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 0), MaximumSize = new Vector2(600, 800) };
    }

    public override void Draw()
    {
        // ImGui edits a local copy; we save only when a value actually changes.
        var days = config.InactiveAfterDays;
        if (ImGui.InputInt("Inactive after (days)", ref days, 30))
        {
            config.InactiveAfterDays = Math.Clamp(days, 30, 3650);
            config.Save();
        }
        ImGui.TextDisabled("A newest achievement older than this counts as likely inactive.");

        ImGui.Spacing();
        var cap = config.LevelCap;
        if (ImGui.InputInt("Level cap", ref cap, 10))
        {
            config.LevelCap = Math.Clamp(cap, 50, 200);
            config.Save();
        }
        ImGui.TextDisabled("Raise this when a new expansion lifts the max level.");

        ImGui.Spacing();
        var delay = config.RequestDelayMs;
        if (ImGui.InputInt("Pause between lookups (ms)", ref delay, 250))
        {
            config.RequestDelayMs = Math.Clamp(delay, 250, 10000);
            config.Save();
        }
        ImGui.TextDisabled("The shortest pause between page loads. The scan slows down by itself if the Lodestone pushes back.");

        ImGui.Spacing();
        var recheck = config.RecheckAfterDays;
        if (ImGui.InputInt("Recheck after (days)", ref recheck, 1))
        {
            config.RecheckAfterDays = Math.Clamp(recheck, 0, 365);
            config.Save();
        }
        ImGui.TextDisabled("A normal Scan skips friends checked more recently than this. A second scan this far apart\nlets the plugin compare profiles and spot quiet activity.");

        ImGui.Spacing();
        var quiet = config.QuietDaysBeforeFlag;
        if (ImGui.InputInt("Quiet days before flagging", ref quiet, 7))
        {
            config.QuietDaysBeforeFlag = Math.Clamp(quiet, 7, 365);
            config.Save();
        }
        ImGui.TextDisabled("A profile with no changes at all for this long moves one step more inactive.");

        ImGui.Spacing();
        var newDays = config.NewFriendDays;
        if (ImGui.InputInt("New friend for (days)", ref newDays, 30))
        {
            config.NewFriendDays = Math.Clamp(newDays, 0, 730);
            config.Save();
        }
        ImGui.TextDisabled("Friends you added more recently than this are never flagged.");
    }
}
