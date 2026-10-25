using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FriendListCleanup.Friends;

/// <summary>A friend as the game knows them.</summary>
/// <param name="Unretrievable">The game shows this friend as "(Unable to Retrieve)": almost always a deleted character.</param>
/// <param name="FriendedUtc">When you became friends. Learned 24 Oct by watching the list: the game's hidden per-friend
/// timestamp is the date the friend was added (it orders the "Oldest/Newest" sort and doesn't change on login).</param>
/// <param name="Online">The game marks this friend as online right now (as of the game's last friend list update).</param>
/// <param name="Zone">Where they are, when online. Empty otherwise.</param>
public readonly record struct FriendEntry(ulong ContentId, string Name, string World, bool Unretrievable, System.DateTime? FriendedUtc,
    bool Online = false, string Zone = "");

/// <summary>
/// Reads the friend list the game client already holds in memory.
/// Read-only: nothing is sent to the game servers.
/// </summary>
public static class FriendReader
{
    /// <summary>
    /// Must run on the game's framework thread (Plugin calls it through Framework.RunOnFrameworkThread).
    /// Returns an empty list if the game hasn't loaded the friend list yet this session;
    /// opening the Friend List window once fixes that.
    /// </summary>
    public static unsafe List<FriendEntry> Read()
    {
        var result = new List<FriendEntry>();

        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return result;

        var worlds = Plugin.DataManager.GetExcelSheet<World>();
        var zones = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        var stamps = proxy->TimeStamps.ToArray(); // indexed by each entry's Sort value (copied: a local function can't hold a Span)

        System.DateTime? Friended(byte sort)
        {
            if (sort >= stamps.Length || stamps[sort] < 1_000_000_000)
                return null;
            return System.DateTimeOffset.FromUnixTimeSeconds(stamps[sort]).UtcDateTime;
        }

        var index = 0;
        foreach (var entry in proxy->CharDataSpan)
        {
            index++;

            // Skip friend requests that haven't been accepted yet.
            if (entry.WaitingForFriendListApproval)
                continue;

            var name = entry.NameString;
            if (string.IsNullOrWhiteSpace(name))
            {
                // The game lists these as "(Unable to Retrieve)". They still use a friend slot, so we keep them.
                // If the game gives no content ID, make a stable stand-in key from the list position.
                var key = entry.ContentId != 0 ? entry.ContentId : 0xFFFF_0000_0000_0000UL + (ulong)index;
                result.Add(new FriendEntry(key, "(Unable to Retrieve)", string.Empty, Unretrievable: true, Friended(entry.Sort)));
                continue;
            }

            if (entry.ContentId == 0)
                continue;

            var world = worlds.TryGetRow(entry.HomeWorld, out var worldRow)
                ? worldRow.Name.ToString()
                : string.Empty;

            var online = (entry.State & InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;
            var zone = online && entry.Location != 0 && zones.TryGetRow(entry.Location, out var zoneRow)
                ? zoneRow.PlaceName.Value.Name.ToString()
                : string.Empty;

            result.Add(new FriendEntry(entry.ContentId, name, world, Unretrievable: false, Friended(entry.Sort), online, zone));
        }

        return result;
    }
}
