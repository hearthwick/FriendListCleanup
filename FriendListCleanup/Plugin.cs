using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FriendListCleanup.Friends;
using FriendListCleanup.Scanning;
using FriendListCleanup.Windows;

namespace FriendListCleanup;

public sealed class Plugin : IDalamudPlugin
{
    // Services Dalamud hands us. Each one is a piece of the game or of Dalamud we're allowed to use.
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;

    private const string ShortCommand = "/flc";
    private const string LongCommand = "/flcleanup";

    public Configuration Configuration { get; }
    public Scanner Scanner { get; }
    public FriendRemover Remover { get; }

    private readonly WindowSystem windowSystem = new("FriendListCleanup");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly ExploreWindow exploreWindow;
    private readonly FriendContextMenu friendContextMenu;
    private readonly FriendCountWarning friendCountWarning;
    private readonly FriendListButton friendListButton;
    public FriendListCleanup.Research.FriendMenuLogger MenuLogger { get; } = new();
    public FriendListCleanup.Research.ChangeWatcher ChangeWatcher { get; } = new();
    public FriendListCleanup.Research.PageSaver PageSaver { get; } = new();
    private bool shortCommandRegistered;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Upgrade();
        Scanner = new Scanner(Configuration);
        Remover = new FriendRemover(Configuration, Scanner);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        exploreWindow = new ExploreWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(exploreWindow);

        // Our verdict in the game's own right-click menu on the Friend List.
        friendContextMenu = new FriendContextMenu(this);

        // A warning in the server info bar when the friend list is nearly full, and a button on the game's Friend List.
        friendCountWarning = new FriendCountWarning(this);
        friendListButton = new FriendListButton(this);

        // /flc is short and handy; /flcleanup is the fallback if another plugin already owns /flc.
        var help = new CommandInfo(OnCommand) { HelpMessage = "Open Friend List Cleanup. Add \"explore\" to see raw friend list data." };
        shortCommandRegistered = CommandManager.AddHandler(ShortCommand, help);
        if (!shortCommandRegistered)
            Log.Warning($"{ShortCommand} is taken by another plugin; use {LongCommand} instead.");
        CommandManager.AddHandler(LongCommand, new CommandInfo(OnCommand) { HelpMessage = "Open Friend List Cleanup." });

        // Every 30 seconds, re-read the friend list from game memory (no server traffic) so we notice
        // friends who are online, even while our window is closed.
        Framework.Update += PassiveCheck;

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += friendListButton.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
    }

    public void Dispose()
    {
        Framework.Update -= PassiveCheck;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= friendListButton.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        windowSystem.RemoveAllWindows();
        friendContextMenu.Dispose();
        friendCountWarning.Dispose();
        MenuLogger.Dispose();
        ChangeWatcher.Dispose();
        PageSaver.Dispose();
        Scanner.Dispose();

        if (shortCommandRegistered)
            CommandManager.RemoveHandler(ShortCommand);
        CommandManager.RemoveHandler(LongCommand);
    }

    /// <summary>
    /// Reads the friend list on the game thread (game memory isn't safe to read from anywhere else)
    /// and hands it to the scanner. Doesn't wait: the window picks up the result on its next frame.
    /// Optionally starts a scan straight after.
    /// </summary>
    public void RefreshFriendList(bool thenScan = false, bool recheckAll = false)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            Scanner.SetFriends(FriendReader.Read());
            if (thenScan)
                Scanner.Start(recheckAll);
        });
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("explore", System.StringComparison.OrdinalIgnoreCase))
        {
            exploreWindow.Toggle();
            return;
        }

        RefreshFriendList();
        mainWindow.Toggle();
    }

    public void ToggleMainUi()
    {
        RefreshFriendList();
        mainWindow.Toggle();
    }

    /// <summary>Opens the main window (doesn't close it if it's already open). Used by the shortcut buttons.</summary>
    public void OpenMainUi()
    {
        RefreshFriendList();
        mainWindow.IsOpen = true;
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    private DateTime nextPassiveCheckUtc = DateTime.MinValue;
    private int passiveChecks;

    private void PassiveCheck(IFramework framework)
    {
        friendCountWarning.Update(); // cheap; it limits itself to once a second

        if (DateTime.UtcNow < nextPassiveCheckUtc)
            return;
        nextPassiveCheckUtc = DateTime.UtcNow.AddSeconds(30);

        Scanner.SetFriends(FriendReader.Read());

        // Save sightings every ~10 minutes so they survive a game restart.
        if (++passiveChecks % 20 == 0)
            lock (Scanner.Gate)
                Configuration.Save();
    }

    /// <summary>Feedback from the last verdict click, shown under the toolbar. Null when there's nothing to say.</summary>
    public string? MenuMessage { get; private set; }

    /// <summary>
    /// What a left-click on a verdict does: opens the game's own right-click menu for that friend.
    /// The game's Friend List window doesn't need to be open; the friend data only has to be loaded once this session.
    /// </summary>
    public void OpenFriendMenu(ulong contentId, string name)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            // Works with the game's Friend List window closed (tested 24 Oct). We never open that window ourselves:
            // doing so skips the Social window around it and makes the game reload the list.
            Report(FriendMenuOpener.Open(contentId, name), name);
        });
    }

    private void Report(FriendMenuOpener.Result result, string name)
    {
        MenuMessage = result switch
        {
            FriendMenuOpener.Result.Opened => null,
            FriendMenuOpener.Result.NotFound => $"{name} isn't in the game's Friend List any more (already removed?).",
            FriendMenuOpener.Result.WrongFriend => $"The game opened a menu for a different friend. Press Escape and right-click {name} in the Friend List instead.",
            FriendMenuOpener.Result.FriendListClosed => "Your friend list isn't loaded yet this session. Open /friendlist once, then try again.",
            _ => "The game's friend list isn't available right now.",
        };
    }

}
