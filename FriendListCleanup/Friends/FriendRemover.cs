using System;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace FriendListCleanup.Friends;

/// <summary>
/// Removes one friend by doing what you'd do by hand, through the game's own menu and confirmation:
///   1. open the game's right-click menu for the friend (FriendMenuOpener),
///   2. find "Remove from Friend List" in that menu and pick it,
///   3. answer Yes on the game's own Yes/No dialog.
/// Every step is checked against the friend we meant; if anything doesn't match, it stops and closes the menu.
/// One call removes exactly one friend, and only when the player asked for it (a click in our window).
///
/// Learned from the Menu logger (24 Oct): picking a menu item sends the menu logic [0, item position, 0];
/// the item positions change per friend (Remove was 1st for a deleted character, 4th for an online friend),
/// so we look Remove up in the menu each time instead of assuming a position. Yes on the dialog = 0, No = 1.
/// </summary>
public sealed unsafe class FriendRemover
{
    /// <summary>The English text of the menu item. Used to look up the same text in the player's game language.</summary>
    private const string RemoveText = "Remove from Friend List";

    /// <summary>The menu item's text in the player's game language, looked up once from the game's own text data.</summary>
    private static string? removeTextForClient;

    // Layout of the game's menu arrays (confirmed from the menu dump on 24 Oct):
    //   text:  slot 0 = item count, slots 1-7 = settings, item texts from slot 8;
    // Slots past the item count hold leftovers from earlier menus and must be ignored.
    // The item IDs are NOT usable: they change from menu to menu (Remove was 113 in one menu and 114 in another),
    // and slot 7 always holds 69, which once fooled us into picking "View Adventurer Plate". So we match on text only.
    private const int FirstTextSlot = 8;

    private readonly Configuration config;
    private readonly Scanning.Scanner scanner;

    public bool Busy { get; private set; }

    /// <summary>What happened last, for the window. Null = nothing to show.</summary>
    public string? Status { get; private set; }
    public bool StatusIsError { get; private set; }

    public FriendRemover(Configuration config, Scanning.Scanner scanner)
    {
        this.config = config;
        this.scanner = scanner;
    }

    /// <summary>Starts removing one friend. Safe to call from the UI; the work happens on the game's thread over a few frames.</summary>
    public void Remove(ulong contentId, string name)
    {
        if (Busy)
            return;

        Busy = true;
        SetStatus($"Removing {name}…", false);
        Plugin.Framework.RunOnFrameworkThread(() => OpenMenu(contentId, name));
    }

    // Step 1: open the game's menu for this friend.
    private void OpenMenu(ulong contentId, string name)
    {
        var result = FriendMenuOpener.Open(contentId, name);
        if (result != FriendMenuOpener.Result.Opened)
        {
            Fail($"Couldn't open the menu for {name} ({result}).", closeMenu: result == FriendMenuOpener.Result.WrongFriend);
            return;
        }

        // Give the game a few frames to finish building the menu.
        Plugin.Framework.RunOnTick(() => PickRemove(contentId, name), delayTicks: 3);
    }

    // Step 2: find "Remove from Friend List" in the menu and pick it.
    private void PickRemove(ulong contentId, string name)
    {
        var friends = AgentFriendlist.Instance();
        var context = AgentContext.Instance();
        if (friends == null || context == null || context->CurrentContextMenu == null)
        {
            Fail("The game's menu didn't open.", closeMenu: false);
            return;
        }

        if (friends->SelectedContentId != contentId)
        {
            Fail($"The game selected a different friend than {name}. Nothing was removed.", closeMenu: true);
            return;
        }

        var menu = context->CurrentContextMenu;
        var count = (int)menu->EventParams[0].UInt;

        // Dump the whole menu to the log (/xllog) so we can see exactly how the game laid it out.
        Plugin.Log.Information($"Menu for {name}: {count} items.");
        for (var slot = 0; slot < menu->EventParams.Length; slot++)
        {
            ref var value = ref menu->EventParams[slot];
            if (value.Type == AtkValueType.Undefined && slot > 0)
                continue;
            var id = slot < menu->EventIds.Length ? menu->EventIds[slot] : (byte)0;
            Plugin.Log.Information($"  slot {slot}: {value.Type} \"{value.GetValueAsString()}\" eventId={id}");
        }

        // Find "Remove from Friend List" by its text, among the real items only. Items are numbered from 0,
        // which is how the game numbers them when you click one.
        var wanted = RemoveTextForClient();
        var removeIndex = -1;
        for (var i = 0; i < count && FirstTextSlot + i < menu->EventParams.Length; i++)
        {
            var text = menu->EventParams[FirstTextSlot + i].GetValueAsString().Trim();
            if (string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, RemoveText, StringComparison.OrdinalIgnoreCase))
            {
                removeIndex = i;
                break;
            }
        }

        if (removeIndex < 0)
        {
            Fail($"Couldn't find \"{RemoveText}\" in {name}'s menu. Nothing was removed. (Menu details are in /xllog.)", closeMenu: true);
            return;
        }

        Plugin.Log.Information($"Picking menu item {removeIndex} for {name}.");
        SendMenuEvent(0, removeIndex); // the same message the menu sends when you click that item
        CloseMenuWindow(); // we skipped the menu window, so close it ourselves (the logic already has our pick)

        // The game now shows its "Remove from Friend List?" dialog. Wait for it, then answer Yes.
        Plugin.Framework.RunOnTick(() => AnswerYes(contentId, name, attempt: 1), delayTicks: 3);
    }

    // Step 3: answer Yes on the game's own confirmation dialog.
    private void AnswerYes(ulong contentId, string name, int attempt)
    {
        var dialog = Plugin.GameGui.GetAddonByName<AtkUnitBase>("SelectYesno");
        var friends = AgentFriendlist.Instance();

        if (dialog == null || !dialog->IsVisible)
        {
            if (attempt < 30) // about half a second
            {
                Plugin.Framework.RunOnTick(() => AnswerYes(contentId, name, attempt + 1), delayTicks: 1);
                return;
            }

            Fail("The game's confirmation dialog didn't appear. Nothing was removed.", closeMenu: false);
            return;
        }

        // Make sure this dialog is the friend list's, for the right friend, and not some other Yes/No.
        if (friends == null || friends->SelectYesNoAddonId != dialog->Id || friends->SelectedContentId != contentId)
        {
            Fail("A different Yes/No dialog is open. Nothing was removed; please answer it yourself.", closeMenu: false);
            return;
        }

        dialog->FireCallbackInt(0); // 0 = Yes, the same as clicking the Yes button

        // The game is about to reload the friend list, which shifts everyone's position.
        // Lock our list now so nobody can click a row until the reload is over.
        scanner.HoldUpdates(TimeSpan.FromSeconds(1.5));
        Busy = false;
        SetStatus($"Removed {name}.", false);
        Plugin.Log.Information($"Removed {name} from the friend list.");
    }

    /// <summary>
    /// The game's own wording for "Remove from Friend List" in the player's language. We find the English text in the
    /// game's text data once, then read the same entry in the client's language. Falls back to English.
    /// </summary>
    private static string RemoveTextForClient()
    {
        if (removeTextForClient is not null)
            return removeTextForClient;

        removeTextForClient = RemoveText;
        try
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<Addon>(ClientLanguage.English))
            {
                if (row.Text.ToString() != RemoveText)
                    continue;
                if (Plugin.DataManager.GetExcelSheet<Addon>().TryGetRow(row.RowId, out var local))
                    removeTextForClient = local.Text.ToString().Trim();
                break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't look up the menu text in the game's language; using English.");
        }

        Plugin.Log.Information($"Looking for the menu item \"{removeTextForClient}\".");
        return removeTextForClient;
    }

    /// <summary>Sends the menu logic the message it gets when you click an item (index ≥ 0) or press Escape (index -1).</summary>
    private static void SendMenuEvent(int action, int index)
    {
        var context = (AtkModuleInterface.AtkEventInterface*)AgentContext.Instance();
        if (context == null)
            return;

        var values = stackalloc AtkValue[3];
        values[0] = default;
        values[1] = default;
        values[2] = default;
        values[0].SetInt(action);
        values[1].SetInt(index);
        values[2].SetUInt(0);

        AtkValue returnValue = default;
        context->ReceiveEvent(&returnValue, values, 3, 0);
        returnValue.ReleaseManagedMemory();
    }

    /// <summary>
    /// Closes the game's right-click menu window if it's showing. Needed because we talk to the menu logic directly,
    /// and the window only closes itself when it's the one that got the click.
    /// </summary>
    private static void CloseMenuWindow()
    {
        var menuWindow = Plugin.GameGui.GetAddonByName<AtkUnitBase>("ContextMenu");
        if (menuWindow != null && menuWindow->IsVisible)
            menuWindow->Close(false); // false = don't report another click to the logic
    }

    private void Fail(string message, bool closeMenu)
    {
        if (closeMenu)
        {
            SendMenuEvent(0, -1); // same as pressing Escape on the menu
            CloseMenuWindow();
        }
        Busy = false;
        SetStatus(message, true);
        Plugin.Log.Warning($"Remove stopped: {message}");
    }

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }
}
