using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FriendListCleanup.Lodestone;

/// <summary>
/// Turns a friend's public Lodestone pages into "fingerprint" sections. Each section is a short summary of things that
/// change when someone plays (gear, EXP, mounts...). Comparing sections between scans tells us they were active,
/// even if they hide their achievements.
///
/// Patterns were written against real pages saved on 24 Oct 2026 (signed out, as the plugin sees them).
/// If Square Enix redesigns the Lodestone, this is the file to update.
/// </summary>
public static partial class ProfileParser
{
    /// <summary>Section names, in the order shown to the player.</summary>
    public static readonly string[] SectionOrder =
        ["Look", "Gear", "Stats", "Current job", "Affiliation", "Bio", "Jobs", "Field content", "Collections", "Achievements"];

    // ---------- Profile page ----------

    public static void ParseProfile(string html, Dictionary<string, string> sections)
    {
        var portrait = Match(PortraitRegex(), html);
        var title = Match(TitleRegex(), html);
        sections["Look"] = $"portrait {FileName(portrait)} · title {title ?? "none"}";

        var gear = GearRegex().Matches(html).Select(m => $"{m.Groups["slot"].Value}:{m.Groups["icon"].Value[..8]}");
        var dyes = DyeRegex().Matches(html).Select(m => m.Groups[1].Value.ToLowerInvariant());
        sections["Gear"] = $"items [{string.Join(",", gear)}] dyes [{string.Join(",", dyes)}]";

        var stats = StatRegex().Matches(html).Select(m => $"{m.Groups[1].Value.Trim()}={m.Groups[2].Value}")
            .Concat(HpMpRegex().Matches(html).Select(m => $"{m.Groups[1].Value.Trim()}={m.Groups[2].Value}"));
        sections["Stats"] = string.Join(",", stats);

        sections["Current job"] = $"{FileName(Match(JobIconRegex(), html))} {Match(ActiveLevelRegex(), html)}";

        var gc = Match(GrandCompanyRegex(), html);
        var fc = Near(html, "character__freecompany__name", FreeCompanyNameRegex(), 600);
        sections["Affiliation"] = $"GC {gc ?? "none"} · FC {fc ?? "none"}";

        var bio = Near(html, "character__selfintroduction\">", BioRegex(), 4000) ?? string.Empty;
        sections["Bio"] = $"text #{ShortHash(bio)}";
    }

    // ---------- Class/Job page ----------

    /// <summary>Returns the levels found, so verdicts can use the top level and Occult Crescent.</summary>
    public static (int TopLevel, int JobsAtCap, int? OccultLevel) ParseClassJob(string html, int levelCap, Dictionary<string, string> sections)
    {
        var jobs = new List<string>();
        var field = new List<string>();
        var levels = new List<int>();
        int? occult = null;

        foreach (Match m in JobRegex().Matches(html))
        {
            var level = m.Groups["lvl"].Value.Trim();
            var name = WebUtility.HtmlDecode(m.Groups["name"].Value.Trim());
            var exp = m.Groups["exp"].Value.Trim();

            if (name is "Elemental Level" or "Resistance Rank" or "Knowledge Level")
            {
                field.Add($"{name} {level} ({exp})");
                if (name == "Knowledge Level" && int.TryParse(level, out var k))
                    occult = k;
                continue;
            }

            if (level is "-" or "")
                continue;
            jobs.Add($"{name} {level} ({exp})");
            if (int.TryParse(level, out var l))
                levels.Add(l);
        }

        foreach (Match m in PhantomRegex().Matches(html))
            field.Add($"{m.Groups[1].Value.Trim()} {m.Groups[2].Value.Trim()} ({m.Groups[3].Value.Trim()})");

        sections["Jobs"] = string.Join(", ", jobs);
        sections["Field content"] = field.Count == 0 ? "none" : string.Join(", ", field);

        return (levels.Count == 0 ? 0 : levels.Max(), levels.Count(l => l >= levelCap), occult);
    }

    // ---------- Mounts / Minions ----------

    public static string ParseCollection(string html, string kind)
    {
        var total = Match(TotalRegex(), html) ?? "?";
        var ids = (kind == "mount" ? MountIdRegex() : MinionIdRegex()).Matches(html).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
        return $"{total} (#{ShortHash(string.Join(",", ids))})";
    }

    // ---------- Achievements (only when public) ----------

    public static (int? Count, int? Points, DateTime? Newest) ParseAchievements(string html)
    {
        int? count = int.TryParse(Match(AchievementTotalRegex(), html), out var c) ? c : null;
        int? points = int.TryParse(Match(AchievementPointsRegex(), html), out var p) ? p : null;

        DateTime? newest = null;
        var listStart = html.IndexOf("entry__achievement--history", StringComparison.Ordinal);
        if (listStart >= 0)
        {
            var t = TimestampRegex().Match(html, listStart);
            if (t.Success && long.TryParse(t.Groups[1].Value, out var unix))
                newest = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }

        return (count, points, newest);
    }

    // ---------- Helpers ----------

    public static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static string? Match(Regex regex, string html)
    {
        var m = regex.Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    /// <summary>Looks for a pattern only in a short stretch of page after a marker, so it can't latch onto something far away.</summary>
    private static string? Near(string html, string marker, Regex regex, int window)
    {
        var i = html.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += marker.Length;
        var m = regex.Match(html.Substring(i, Math.Min(window, html.Length - i)));
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    private static string FileName(string? url) => url is null ? "none" : url[(url.LastIndexOf('/') + 1)..];

    [GeneratedRegex("""js__image_popup[^>]*>\s*<img src="([^"?]+)""")]
    private static partial Regex PortraitRegex();

    [GeneratedRegex("""frame__chara__title">([^<]*)<""")]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""class="icon-c--(?<slot>\d+) [^"]*js__db_tooltip"[^>]*>\s*<img src="[^"]*/itemicon/[0-9a-f]{2}/(?<icon>[0-9a-f]{8,})""")]
    private static partial Regex GearRegex();

    [GeneratedRegex("""mirage_staining painted_cover" style="background-color: (#[0-9a-fA-F]{6})""")]
    private static partial Regex DyeRegex();

    [GeneratedRegex("""<th[^>]*>(?:<span[^>]*>)?([A-Za-z ]+?)(?:</span>)?</th><td[^>]*>(\d+)</td>""")]
    private static partial Regex StatRegex();

    [GeneratedRegex("""character__param__text[^>]*>([^<]+)</p><span>(\d+)</span>""")]
    private static partial Regex HpMpRegex();

    [GeneratedRegex("""character__class_icon"><img src="([^"?]+)""")]
    private static partial Regex JobIconRegex();

    [GeneratedRegex("""character__class__data"><p>([^<]*)<""")]
    private static partial Regex ActiveLevelRegex();

    [GeneratedRegex("""Grand Company</p><p class="character-block__name">([^<]*)<""")]
    private static partial Regex GrandCompanyRegex();

    [GeneratedRegex("""<a [^>]*>([^<]*)</a>""")]
    private static partial Regex FreeCompanyNameRegex();

    [GeneratedRegex("""^(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex BioRegex();

    [GeneratedRegex("""<div class="character__job__level">(?<lvl>[^<]*)</div>\s*<div class="character__job__name[^>]*>(?<name>[^<]*)</div>\s*<div class="character__job__exp">(?<exp>[^<]*)</div>""")]
    private static partial Regex JobRegex();

    [GeneratedRegex("""character__support_job__name">([^<]*)</p>\s*<p class="character__support_job__level">([^<]*)</p>\s*<p class="character__support_job__(?:exp|master)">([^<]*)</p>""")]
    private static partial Regex PhantomRegex();

    [GeneratedRegex("""Total: <span>(\d+)</span>""")]
    private static partial Regex TotalRegex();

    [GeneratedRegex("""/mount/tooltip/([0-9a-f]{40})""")]
    private static partial Regex MountIdRegex();

    [GeneratedRegex("""/minion/tooltip/([0-9a-f]{40})""")]
    private static partial Regex MinionIdRegex();

    [GeneratedRegex("""parts__total">(\d+) Total<""")]
    private static partial Regex AchievementTotalRegex();

    [GeneratedRegex("""achievement__point">(\d+)<""")]
    private static partial Regex AchievementPointsRegex();

    [GeneratedRegex(@"ldst_strftime\((\d{9,})")]
    private static partial Regex TimestampRegex();
}
