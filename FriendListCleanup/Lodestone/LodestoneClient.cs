using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FriendListCleanup.Lodestone;

/// <summary>
/// Fetches public Lodestone pages, politely paced. Finding a character by name lives here;
/// reading the pages' contents lives in ProfileParser.
/// </summary>
public sealed partial class LodestoneClient : IDisposable
{
    private const string BaseUrl = "https://na.finalfantasyxiv.com";

    private readonly HttpClient http;

    public LodestoneClient(Configuration config)
    {
        this.config = config;
        http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        // A normal browser user agent: the Lodestone serves some tools differently without one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
    }

    /// <summary>
    /// Finds a character's Lodestone ID by exact name on their home world. Null if not found.
    /// </summary>
    public async Task<string?> FindCharacterIdAsync(string name, string world, CancellationToken ct)
    {
        // First an exact-phrase search: the Lodestone treats a quoted name as a phrase (a short, common name in quotes gave
        // 5 results instead of 799). Then, just in case, the plain search, which matches partial names
        // 50 per page, so we read a few pages of it.
        var found = await SearchAsync($"\"{name}\"", name, world, maxPages: 2, ct);
        return found ?? await SearchAsync(name, name, world, maxPages: 5, ct);
    }

    private async Task<string?> SearchAsync(string query, string name, string world, int maxPages, CancellationToken ct)
    {
        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"/lodestone/character/?q={Uri.EscapeDataString(query)}&worldname={Uri.EscapeDataString(world)}&page={page}";
            var (status, html) = await GetAsync(url, ct);
            if (status != HttpStatusCode.OK || html is null)
                return null;

            var entries = SearchEntryRegex().Matches(html)
                .Select(m => (Id: m.Groups["id"].Value, Name: WebUtility.HtmlDecode(m.Groups["name"].Value).Trim()))
                .ToList();

            var exact = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact.Id is not null)
            {
                if (page > 1 || query != name)
                    Plugin.Log.Information($"Found {name} (search: {query}, page {page}).");
                return exact.Id;
            }

            if (entries.Count < 50) // last page of results
                break;
        }

        return null;
    }

    /// <summary>Gets one of a character's pages ("" = profile, "class_job/", "mount/", "minion/", "achievement/").</summary>
    public Task<(HttpStatusCode Status, string? Html)> GetPageAsync(string lodestoneId, string page, CancellationToken ct)
        => GetAsync($"/lodestone/character/{lodestoneId}/{page}", ct);

    // ---------- Polite, adaptive pacing ----------
    // Every request waits its turn. We start at the configured pause and slow down automatically
    // (doubling the pause) whenever the Lodestone pushes back, then ease back towards the configured
    // pause after a run of successful requests. The configured pause is never undercut.

    private readonly Configuration config;
    private int currentDelayMs;
    private int successStreak;
    private DateTime nextRequestUtc = DateTime.MinValue;

    /// <summary>The pause currently used between requests, for display.</summary>
    public int CurrentDelayMs => Math.Max(currentDelayMs, MinDelayMs);

    private int MinDelayMs => Math.Max(250, config.RequestDelayMs);

    private async Task WaitYourTurn(CancellationToken ct)
    {
        var wait = nextRequestUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
        nextRequestUtc = DateTime.UtcNow.AddMilliseconds(CurrentDelayMs);
    }

    private void SlowDown()
    {
        currentDelayMs = Math.Min(Math.Max(CurrentDelayMs, 500) * 2, 8000);
        successStreak = 0;
        Plugin.Log.Warning($"Lodestone pushed back; pausing {currentDelayMs} ms between requests for now.");
    }

    private void Succeeded()
    {
        if (++successStreak >= 20 && currentDelayMs > MinDelayMs)
        {
            currentDelayMs = Math.Max(MinDelayMs, (int)(currentDelayMs * 0.8));
            successStreak = 0;
        }
    }

    /// <summary>
    /// One GET request, paced. If the Lodestone says "slow down" (HTTP 429), we slow down, wait 30 seconds and try once more.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Html)> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await WaitYourTurn(ct);
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                SlowDown();
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
            }
            else
            {
                Succeeded();
            }

            var html = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
            return (response.StatusCode, html);
        }

        return (HttpStatusCode.TooManyRequests, null);
    }

    public void Dispose() => http.Dispose();

    // Search results: <a href="/lodestone/character/12345/" class="entry__link"> ... <p class="entry__name">Name</p>
    [GeneratedRegex("""href="/lodestone/character/(?<id>\d+)/"\s+class="entry__link".*?<p class="entry__name">(?<name>[^<]+)</p>""",
        RegexOptions.Singleline)]
    private static partial Regex SearchEntryRegex();


}
