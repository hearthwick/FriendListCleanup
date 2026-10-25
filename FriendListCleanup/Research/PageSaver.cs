using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace FriendListCleanup.Research;

/// <summary>
/// RESEARCH TOOL. Downloads a friend's public Lodestone pages exactly as served and saves them to the plugin's
/// config folder, so we can design the profile fingerprint from the real page structure instead of guessing.
/// Same public pages anyone can open in a browser; one friend at a time, with a pause between pages.
/// </summary>
public sealed class PageSaver : IDisposable
{
    private static readonly string[] Pages = ["", "class_job/", "mount/", "minion/", "achievement/"];

    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri("https://na.finalfantasyxiv.com"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    public bool Busy { get; private set; }
    public string? Status { get; private set; }

    public PageSaver()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
    }

    /// <summary>Saves each friend's pages in turn, one friend at a time.</summary>
    public void SaveAll(List<(string Id, string Name)> friends)
    {
        if (Busy || friends.Count == 0)
            return;
        Busy = true;
        _ = Task.Run(async () =>
        {
            var saved = 0;
            string? folder = null;
            try
            {
                foreach (var (id, name) in friends)
                {
                    Status = $"Saving pages for {name} ({saved + 1} of {friends.Count})…";
                    folder = await SaveOne(id, name);
                    saved++;
                }

                Status = $"Saved pages for {saved} friend(s) to {Path.GetDirectoryName(folder)}";
                Plugin.Log.Information($"[PageSaver] {Status}");
            }
            catch (Exception ex)
            {
                Status = $"Stopped after {saved} friend(s): {ex.Message}";
                Plugin.Log.Warning(ex, "[PageSaver] failed");
            }
            finally
            {
                Busy = false;
            }
        });
    }

    private async Task<string> SaveOne(string lodestoneId, string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "pages", $"{safe}_{DateTime.Now:yyyyMMdd-HHmm}");
        Directory.CreateDirectory(dir);

        foreach (var page in Pages)
        {
            using var response = await http.GetAsync($"/lodestone/character/{lodestoneId}/{page}");
            var html = await response.Content.ReadAsStringAsync();
            var file = (page.Length == 0 ? "profile" : page.TrimEnd('/')) + $"_{(int)response.StatusCode}.html";
            await File.WriteAllTextAsync(Path.Combine(dir, file), html);
            await Task.Delay(1000);
        }

        return dir;
    }

    public void Dispose() => http.Dispose();
}
