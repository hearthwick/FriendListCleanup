using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FriendListCleanup.Friends;

/// <summary>
/// Everything the game client holds about one friend, as raw as we can show it.
/// Used by the Explore window to find signals the normal friend list UI doesn't display.
/// </summary>
public sealed record FriendDumpRow(
    int Index,
    ulong ContentId,
    string Name,
    string HomeWorld,
    string CurrentWorld,
    string Status,
    string Group,
    bool OtherServer,
    string Location,
    string GrandCompany,
    string ClientLanguage,
    string Languages,
    int Sex,
    string Job,
    string FcTag,
    int Sort,
    int RawTimestamp,
    string TimestampAsDate);

public static class FriendDump
{
    public static readonly string[] Columns =
    [
        "#", "Content ID", "Name", "Home world", "Current world", "Status flags", "Group", "Other server", "Location",
        "Grand Company", "Client language", "Languages", "Sex", "Job", "FC tag", "Sort", "Raw timestamp", "Timestamp as date",
    ];

    /// <summary>Reads every field. Must run on the framework thread.</summary>
    public static unsafe List<FriendDumpRow> Capture()
    {
        var rows = new List<FriendDumpRow>();
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return rows;

        var worlds = Plugin.DataManager.GetExcelSheet<World>();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var zones = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        var timestamps = proxy->TimeStamps;

        string WorldName(ushort id) => id != 0 && worlds.TryGetRow(id, out var w) ? w.Name.ToString() : (id == 0 ? "" : $"#{id}");

        var i = 0;
        foreach (var e in proxy->CharDataSpan)
        {
            i++;
            if (e.ContentId == 0)
                continue;

            var ts = e.Sort < timestamps.Length ? timestamps[e.Sort] : 0;
            var job = e.Job != 0 && jobs.TryGetRow(e.Job, out var j) ? j.Abbreviation.ToString() : (e.Job == 0 ? "" : $"#{e.Job}");
            var zone = e.Location != 0 && zones.TryGetRow(e.Location, out var z) ? z.PlaceName.Value.Name.ToString() : (e.Location == 0 ? "" : $"#{e.Location}");

            rows.Add(new FriendDumpRow(
                i,
                e.ContentId,
                string.IsNullOrWhiteSpace(e.NameString) ? "(Unable to Retrieve)" : e.NameString,
                WorldName(e.HomeWorld),
                WorldName(e.CurrentWorld),
                e.State.ToString(),
                e.Group.ToString(),
                e.IsOtherServer,
                zone,
                e.GrandCompany.ToString(),
                e.ClientLanguage.ToString(),
                e.Languages.ToString(),
                e.Sex,
                job,
                e.FCTagString,
                e.Sort,
                ts,
                TimestampAsDate(ts)));
        }

        return rows;
    }

    /// <summary>
    /// We don't know yet what these numbers mean. If they look like Unix time, show the date so we can compare
    /// against what we know (when you added someone, when they were last online...).
    /// </summary>
    public static string TimestampAsDate(int ts)
    {
        if (ts < 1_000_000_000) // before Sept 2001: not a plausible date
            return "";
        return DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>Writes the rows as a CSV in the plugin's config folder and returns the file path.</summary>
    public static string ExportCsv(List<FriendDumpRow> rows)
    {
        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"friend-dump-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        static string Esc(object? v)
        {
            var s = v?.ToString() ?? "";
            return s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(Esc)));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", new object[]
            {
                r.Index, r.ContentId, r.Name, r.HomeWorld, r.CurrentWorld, r.Status, r.Group, r.OtherServer, r.Location,
                r.GrandCompany, r.ClientLanguage, r.Languages, r.Sex, r.Job, r.FcTag, r.Sort, r.RawTimestamp, r.TimestampAsDate,
            }.Select(Esc)));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
