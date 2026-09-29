using System.Globalization;
using GameSite.Server.Core;
using GameSite.Server.Data;

namespace GameSite.Server.Catalog;

/// <summary>一覧画面の絞り込み条件。</summary>
public sealed record GameQuery(string? Q, string? Tag, string? Sort, string? View)
{
    public const string ViewAll = "all";
    public const string ViewFavorites = "fav";
    public const string ViewRecent = "recent";

    public string NormalizedView => View is ViewFavorites or ViewRecent ? View : ViewAll;
    public string NormalizedSort => Sort is "name" or "popular" ? Sort : "new";
    public string? NormalizedTag => string.IsNullOrWhiteSpace(Tag) || Tag == "all" ? null : Tag;
}

/// <summary>検索・絞り込み・並び替えのロジック。</summary>
public static class GameSearch
{
    private static readonly StringComparer JapaneseComparer = StringComparer.Create(CultureInfo.GetCultureInfo("ja-JP"), ignoreCase: true);

    public static IReadOnlyList<Game> Run(IEnumerable<Game> games, GameQuery query, PlayerSnapshot player, IReadOnlyDictionary<string, long> totalPlays)
    {
        var words = TextNormalizer.Tokenize(query.Q);
        var tag = query.NormalizedTag;
        var view = query.NormalizedView;

        var list = games
            .Where(g => g.Listed)
            .Where(g => tag is null || g.Manifest.Tags.Contains(tag))
            .Where(g => words.All(w => g.SearchText.Contains(w, StringComparison.Ordinal)))
            .Where(g => view switch
            {
                GameQuery.ViewFavorites => player.Favorites.Contains(g.Id),
                GameQuery.ViewRecent => player.Plays.ContainsKey(g.Id),
                _ => true,
            });

        if (view == GameQuery.ViewRecent)
            return list.OrderByDescending(g => player.Plays[g.Id].LastPlayedAt).ToList();

        // 遊べるゲームを常に先に表示し、その中で指定の順に並べる
        var ordered = list.OrderByDescending(g => g.Playable);
        ordered = query.NormalizedSort switch
        {
            "name" => ordered.ThenBy(g => g.SortKey, JapaneseComparer),
            "popular" => ordered.ThenByDescending(g => totalPlays.GetValueOrDefault(g.Id)).ThenByDescending(g => g.Added),
            _ => ordered.ThenByDescending(g => g.Added).ThenBy(g => g.SortKey, JapaneseComparer),
        };
        return ordered.ToList();
    }

    /// <summary>タグの一覧（使用数の多い順）。</summary>
    public static IReadOnlyList<(string Name, int Count)> Tags(IEnumerable<Game> games) =>
        games.Where(g => g.Listed)
            .SelectMany(g => g.Manifest.Tags.Distinct())
            .GroupBy(t => t)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, JapaneseComparer)
            .ToList();

    /// <summary>トップに出すピックアップ：featured → 最新の遊べるゲーム → 何か 1 本。</summary>
    public static Game? Featured(IEnumerable<Game> games)
    {
        var listed = games.Where(g => g.Listed).ToList();
        return listed.FirstOrDefault(g => g.Manifest.Featured && g.Playable)
            ?? listed.Where(g => g.Playable).MaxBy(g => g.Added)
            ?? listed.FirstOrDefault(g => g.Manifest.Featured)
            ?? listed.MaxBy(g => g.Added);
    }
}
