using System;
using System.Collections.Generic;
using System.Linq;

namespace Arcademia.Link
{
    public sealed class ArcademiaUser
    {
        public string Id { get; set; }
        public string Username { get; set; }
    }

    public sealed class ArcademiaAchievement
    {
        public int Id { get; set; }
        public string ApiName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public bool Hidden { get; set; }
        public bool Revealed { get; set; }
        public bool Unlocked { get; set; }
        public DateTime? UnlockedAt { get; set; }
        public double GlobalPercent { get; set; }
    }

    public sealed class ArcademiaGame
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ThumbnailUrl { get; set; }
        public int AchievementCount { get; set; }
        public int UnlockedCount { get; set; }
        public List<ArcademiaAchievement> Achievements { get; set; } = new List<ArcademiaAchievement>();

        public double CompletionPercent =>
            AchievementCount == 0 ? 0 : Math.Round(100.0 * UnlockedCount / AchievementCount, 1);
    }

    public sealed class AchievementSnapshot
    {
        public ArcademiaUser User { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int GameCount { get; set; }
        public int AchievementCount { get; set; }
        public int UnlockedCount { get; set; }
        public List<ArcademiaGame> Games { get; set; } = new List<ArcademiaGame>();

        public IEnumerable<(ArcademiaGame Game, ArcademiaAchievement Achievement)> Unlocked() =>
            Games.SelectMany(g => g.Achievements.Where(a => a.Unlocked).Select(a => (g, a)));

        public IReadOnlyList<(ArcademiaGame Game, ArcademiaAchievement Achievement)> NewlyUnlockedSince(
            AchievementSnapshot previous
        )
        {
            var known = new HashSet<int>(previous?.Unlocked().Select(u => u.Achievement.Id) ?? Enumerable.Empty<int>());
            return Unlocked().Where(u => !known.Contains(u.Achievement.Id)).OrderBy(u => u.Achievement.UnlockedAt).ToList();
        }
    }

    public sealed class StoredTokens
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Scope { get; set; }
    }
}
