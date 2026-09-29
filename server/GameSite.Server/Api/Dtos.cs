using GameSite.Server.Catalog;
using GameSite.Server.Data;

namespace GameSite.Server.Api;

/// <summary>一覧のカード 1 枚分。</summary>
public sealed record GameCardDto(
    string Id,
    string Title,
    string? Summary,
    string Icon,
    string Color,
    string Url,
    string? ThumbnailUrl,
    string[] Tags,
    string? Genre,
    string? Players,
    string? PlayTime,
    string Status,
    bool Playable,
    bool IsNew,
    bool Featured,
    bool IsFavorite,
    int MyPlays,
    DateTimeOffset? LastPlayedAt,
    long TotalPlays)
{
    public static GameCardDto From(Game g, PlayerSnapshot player, IReadOnlyDictionary<string, long> totals, DateOnly today, int newDays)
    {
        var mine = player.Plays.GetValueOrDefault(g.Id);
        return new GameCardDto(
            g.Id, g.Title, g.Manifest.Summary ?? g.Manifest.Description, g.Icon, g.Color, g.Url, g.ThumbnailUrl,
            g.Manifest.Tags, g.Manifest.Genre, g.Manifest.Players, g.Manifest.PlayTime,
            StatusName(g.Status), g.Playable, g.IsNew(today, newDays), g.Manifest.Featured,
            player.Favorites.Contains(g.Id), mine?.Count ?? 0, mine?.LastPlayedAt, totals.GetValueOrDefault(g.Id));
    }

    public static string StatusName(GameStatus s) => s switch
    {
        GameStatus.Beta => "beta",
        GameStatus.ComingSoon => "coming-soon",
        GameStatus.Hidden => "hidden",
        _ => "published",
    };
}

public sealed record TagDto(string Name, int Count);

public sealed record ModeDto(string Id, string Label);

public sealed record LeaderboardDto(string Mode, IReadOnlyList<LeaderboardEntry> Entries);

public sealed record GameDetailDto(
    GameCardDto Game,
    string? Description,
    string[] Controls,
    string[] HowToPlay,
    DateOnly? Added,
    DateOnly? Updated,
    IReadOnlyList<ModeDto> Modes,
    LeaderboardDto? Leaderboard);

public sealed record HomeDto(
    string Title,
    GameCardDto? Featured,
    HomeStatsDto Stats,
    IReadOnlyList<GameCardDto> Recent,
    IReadOnlyList<TagDto> Tags);

public sealed record HomeStatsDto(int Published, int ComingSoon, DateOnly? LastUpdated);

public sealed record GameListDto(IReadOnlyList<GameCardDto> Items, int Total);

// ---- リクエスト ----
public sealed record CreateSessionRequest(System.Text.Json.JsonElement Options);
public sealed record ActionRequest(System.Text.Json.JsonElement Action);
public sealed record ScoreRequest(string? Name);
