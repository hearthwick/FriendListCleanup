using System;
using System.Collections.Generic;
using System.Linq;

namespace FriendListCleanup.Verdicts;

/// <summary>
/// Estimates when a character was created from their Lodestone ID.
///
/// Lodestone IDs are handed out in order as characters are created, so a higher ID means a newer character.
/// Friends with public achievements give us reference points: their oldest achievement is close to their creation date.
/// For anyone else, we look at the reference characters with the nearest IDs and take the middle of their dates.
/// It's an estimate built only from your own friend list, and the reason text says so.
/// </summary>
public sealed class CreationEstimator
{
    private readonly List<(long Id, DateTime Created)> refs;

    private CreationEstimator(List<(long, DateTime)> refs) => this.refs = refs;

    public int ReferenceCount => refs.Count;

    public static CreationEstimator Build(IEnumerable<FriendRecord> records)
    {
        var list = records
            .Where(r => r.OldestAchievementUtc is not null && long.TryParse(r.LodestoneId, out _))
            .Select(r => (long.Parse(r.LodestoneId!), r.OldestAchievementUtc!.Value))
            .OrderBy(x => x.Item1)
            .ToList();
        return new CreationEstimator(list);
    }

    /// <summary>A short note like "character created around Mar 2023", or null if we can't say.</summary>
    public string? Describe(FriendRecord r)
    {
        if (refs.Count < 5 || !long.TryParse(r.LodestoneId, out var id))
            return null;

        // Their own oldest achievement is better than any estimate.
        if (r.OldestAchievementUtc is { } own)
            return $"character created around {own.ToLocalTime():MMM yyyy}";

        if (id > refs[^1].Id)
            return $"newer character than all {refs.Count} reference friends (newest created around {refs[^1].Created.ToLocalTime():MMM yyyy})";

        // The 5 reference characters with the closest IDs; the middle date of those.
        var nearest = refs.OrderBy(x => Math.Abs(x.Id - id)).Take(5).Select(x => x.Created).OrderBy(d => d).ToList();
        var estimate = nearest[nearest.Count / 2];
        return $"character created around {estimate.ToLocalTime():MMM yyyy} (estimated)";
    }
}
