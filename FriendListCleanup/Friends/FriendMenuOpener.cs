using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListCleanup.Friends;

/// <summary>
/// Opens the game's own right-click menu for a friend, exactly as if you'd right-clicked them in the Friend List.
///
/// How we know what to send: the Menu logger (Explore window) showed that a right-click in the Friend List sends the
/// friend list logic one message with three values: [1 = "open menu", list position, name]. We send that same message.
/// It only opens a menu. Removing still happens through the game's own "Remove from Friend List" option and its
/// Yes/No confirmation, so nothing reaches the servers unless you confirm it yourself.
/// </summary>
public static unsafe class FriendMenuOpener
{
    public enum Result { Opened, FriendListClosed, NotFound, WrongFriend, Unavailable }

    /// <summary>Must run on the framework thread.</summary>
    public static Result Open(ulong contentId, string name)
    {
        var agent = AgentFriendlist.Instance();
        var proxy = InfoProxyFriendList.Instance();
        if (agent == null || proxy == null)
            return Result.Unavailable;

        // The Friend List window doesn't need to be open (tested 24 Oct), but we log whether it was, for troubleshooting.
        var listOpen = agent->IsAgentActive();

        // The friend data stays in memory after the list has been loaded once this session; without it there's nothing to target.
        if (proxy->CharDataSpan.Length == 0)
            return Result.FriendListClosed;

        // Find the friend's position the same way the game does (its own lookup function),
        // so the position matches what the Friend List window is showing right now.
        var count = (uint)proxy->CharDataSpan.Length;
        var index = -1;
        for (uint i = 0; i < count; i++)
        {
            if (proxy->GetContentIdForEntry(i) == contentId)
            {
                index = (int)i;
                break;
            }
        }

        if (index < 0)
            return Result.NotFound;

        // Build the three values the game sends on a right-click.
        var values = stackalloc AtkValue[3];
        values[0] = default;
        values[1] = default;
        values[2] = default;
        values[0].SetInt(1);          // 1 = open the menu
        values[1].SetInt(index);      // position in the list
        values[2].SetManagedString(name);

        AtkValue returnValue = default;
        ((AtkModuleInterface.AtkEventInterface*)agent)->ReceiveEvent(&returnValue, values, 3, 0);

        values[2].ReleaseManagedMemory();
        returnValue.ReleaseManagedMemory();

        // Safety check: did the game pick the friend we meant? If not, say so loudly.
        if (agent->SelectedContentId != contentId)
        {
            Plugin.Log.Warning($"Menu opened for the wrong friend: wanted {name} ({contentId}), game selected {agent->SelectedContentId}. List open: {listOpen}.");
            return Result.WrongFriend;
        }

        Plugin.Log.Information($"Opened the game's menu for {name}. Friend List window open: {listOpen}.");
        return Result.Opened;
    }
}
