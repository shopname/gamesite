using GameSite.Server.Catalog;
using GameSite.Server.Core;
using GameSite.Server.Data;
using GameSite.Server.Games;
using Microsoft.Extensions.Options;

namespace GameSite.Server.Api;

/// <summary>
/// ブラウザ（HTML/CSS/JS）から呼ばれる API。
/// 画面の表示以外のロジック（検索・並び替え・お気に入り・履歴・ゲーム進行・得点計算）はすべてここから C# で処理する。
/// </summary>
public static class ApiEndpoints
{
    public const string SessionPolicy = "session";

    public static void MapGameSiteApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------------- ホーム・一覧 ----------------
        api.MapGet("/home", async (HttpContext ctx, GameCatalog catalog, PlayerStore store, IOptions<SiteOptions> site) =>
        {
            var (player, totals) = await LoadPlayerAsync(ctx, store);
            var games = catalog.All.Where(g => g.Listed).ToList();
            var card = Mapper(catalog, site.Value, player, totals);

            var recent = GameSearch.Run(games, new GameQuery(null, null, null, GameQuery.ViewRecent), player, totals)
                .Where(g => g.Playable).Take(8).Select(card).ToList();

            return new HomeDto(
                site.Value.Title,
                GameSearch.Featured(games) is { } f ? card(f) : null,
                new HomeStatsDto(
                    games.Count(g => g.Playable),
                    games.Count(g => g.Status == GameStatus.ComingSoon),
                    games.Max(g => g.LastChanged)),
                recent,
                GameSearch.Tags(games).Select(t => new TagDto(t.Name, t.Count)).ToList());
        });

        api.MapGet("/games", async (HttpContext ctx, [AsParameters] GameQuery query, GameCatalog catalog, PlayerStore store, IOptions<SiteOptions> site) =>
        {
            var (player, totals) = await LoadPlayerAsync(ctx, store);
            var items = GameSearch.Run(catalog.All, query, player, totals);
            return new GameListDto(
                items.Select(Mapper(catalog, site.Value, player, totals)).ToList(),
                catalog.All.Count(g => g.Listed));
        });

        api.MapGet("/games/random", async (HttpContext ctx, [AsParameters] GameQuery query, GameCatalog catalog, PlayerStore store, IOptions<SiteOptions> site, string? exclude) =>
        {
            var (player, totals) = await LoadPlayerAsync(ctx, store);
            var pool = GameSearch.Run(catalog.All, query, player, totals).Where(g => g.Playable).ToList();
            if (pool.Count == 0) pool = catalog.All.Where(g => g.Listed && g.Playable).ToList();
            if (pool.Count > 1 && exclude is not null) pool.RemoveAll(g => g.Id == exclude);
            if (pool.Count == 0) return Results.Problem("遊べるゲームがまだありません。", statusCode: 404);

            var pick = pool[Random.Shared.Next(pool.Count)];
            return Results.Ok(Mapper(catalog, site.Value, player, totals)(pick));
        });

        api.MapGet("/games/{id}", async (string id, HttpContext ctx, GameCatalog catalog, PlayerStore store, IOptions<SiteOptions> site) =>
        {
            var game = catalog.Find(id);
            if (game is null || !game.Listed) return NotFound();

            var (player, totals) = await LoadPlayerAsync(ctx, store);
            var m = game.Manifest;
            LeaderboardDto? board = null;
            if (game.Playable)
            {
                var mode = game.ResolveMode(null);
                board = new LeaderboardDto(mode, await store.GetLeaderboardAsync(game.Id, mode, 5, PlayerIdentity.From(ctx)));
            }

            return Results.Ok(new GameDetailDto(
                Mapper(catalog, site.Value, player, totals)(game),
                m.Description ?? m.Summary,
                m.Controls,
                m.HowToPlay,
                game.Added,
                game.Updated,
                m.Modes.Select(x => new ModeDto(x.Id, x.Label)).ToList(),
                board));
        });

        api.MapGet("/games/{id}/leaderboard", async (string id, string? mode, int? limit, HttpContext ctx, GameCatalog catalog, PlayerStore store) =>
        {
            var game = catalog.Find(id);
            if (game is null || !game.Listed) return NotFound();
            var resolved = game.ResolveMode(mode);
            var entries = await store.GetLeaderboardAsync(game.Id, resolved, limit ?? 10, PlayerIdentity.From(ctx));
            return Results.Ok(new LeaderboardDto(resolved, entries));
        });

        // ---------------- お気に入り・履歴 ----------------
        api.MapPut("/me/favorites/{id}", (string id, HttpContext ctx, GameCatalog catalog, PlayerStore store) =>
            SetFavoriteAsync(id, true, ctx, catalog, store));

        api.MapDelete("/me/favorites/{id}", (string id, HttpContext ctx, GameCatalog catalog, PlayerStore store) =>
            SetFavoriteAsync(id, false, ctx, catalog, store));

        api.MapDelete("/me/history", async (HttpContext ctx, PlayerStore store) =>
        {
            if (PlayerIdentity.From(ctx) is not Guid player) return MissingPlayer();
            await store.ClearHistoryAsync(player);
            return Results.NoContent();
        });

        // ---------------- ゲーム進行 ----------------
        var sessions = api.MapGroup("/games/{id}/sessions").RequireRateLimiting(SessionPolicy);

        sessions.MapPost("", async (string id, CreateSessionRequest? body, HttpContext ctx,
            GameCatalog catalog, GameRegistry registry, SessionStore store, PlayerStore players, TimeProvider time) =>
        {
            if (PlayerIdentity.From(ctx) is not Guid player) return MissingPlayer();
            var game = catalog.Find(id);
            if (game is null || !game.Playable) return NotFound();
            if (registry.Find(id) is not IGameLogic logic)
                return Results.Problem("このゲームのロジックがサーバーに登録されていません。", statusCode: 501);

            var session = store.Create(id, player, logic.CreateSession(body?.Options ?? default, new GameContext(Random.Shared, time)));
            await players.RecordPlayAsync(player, id); // 1 回ゲームを始めた = 1 プレイ
            return Results.Ok(new { sessionId = session.Id, state = session.Game.GetView() });
        });

        sessions.MapPost("/{sessionId}/actions", (string id, string sessionId, ActionRequest body, HttpContext ctx, SessionStore store) =>
        {
            if (PlayerIdentity.From(ctx) is not Guid player) return MissingPlayer();
            if (store.Get(sessionId, id, player) is not ActiveSession session) return SessionExpired();

            lock (session.Gate)
            {
                var outcome = session.Game.Apply(body.Action);
                if (!outcome.Ok) return Results.Problem(outcome.Error, statusCode: 400, extensions: new Dictionary<string, object?> { ["state"] = session.Game.GetView() });
                return Results.Ok(new { state = session.Game.GetView(), events = outcome.Events });
            }
        });

        sessions.MapPost("/{sessionId}/score", async (string id, string sessionId, ScoreRequest body, HttpContext ctx,
            SessionStore store, PlayerStore players) =>
        {
            if (PlayerIdentity.From(ctx) is not Guid player) return MissingPlayer();
            if (store.Get(sessionId, id, player) is not ActiveSession session) return SessionExpired();

            int score;
            lock (session.Gate)
            {
                if (!session.Game.IsFinished || session.Game.FinalScore is null)
                    return Results.Problem("ゲームが終わってから登録してください。", statusCode: 400);
                if (session.ScoreSubmitted)
                    return Results.Problem("このプレイの得点は登録済みです。", statusCode: 409);
                session.ScoreSubmitted = true;
                score = session.Game.FinalScore.Value;
            }

            var mode = session.Game.Mode;
            var rank = await players.AddScoreAsync(id, mode, player, PlayerIdentity.SanitizeName(body.Name), score);
            var board = await players.GetLeaderboardAsync(id, mode, 10, player);
            return Results.Ok(new { rank, score, leaderboard = new LeaderboardDto(mode, board) });
        });
    }

    // ------------------------------------------------------------------
    private static async Task<(PlayerSnapshot, IReadOnlyDictionary<string, long>)> LoadPlayerAsync(HttpContext ctx, PlayerStore store)
    {
        var player = await store.GetSnapshotAsync(PlayerIdentity.From(ctx));
        var totals = await store.GetTotalPlaysAsync();
        return (player, totals);
    }

    private static Func<Game, GameCardDto> Mapper(GameCatalog catalog, SiteOptions site, PlayerSnapshot player, IReadOnlyDictionary<string, long> totals)
    {
        var today = catalog.Today;
        return g => GameCardDto.From(g, player, totals, today, site.NewBadgeDays);
    }

    private static async Task<IResult> SetFavoriteAsync(string id, bool on, HttpContext ctx, GameCatalog catalog, PlayerStore store)
    {
        if (PlayerIdentity.From(ctx) is not Guid player) return MissingPlayer();
        if (catalog.Find(id) is null) return NotFound();
        await store.SetFavoriteAsync(player, id, on);
        return Results.Ok(new { id, isFavorite = on });
    }

    private static IResult NotFound() => Results.Problem("ゲームが見つかりません。", statusCode: 404);

    private static IResult MissingPlayer() =>
        Results.Problem($"{PlayerIdentity.HeaderName} ヘッダーが必要です。", statusCode: 400);

    private static IResult SessionExpired() =>
        Results.Problem("しばらく操作がなかったため、ゲームが終了しました。もう一度はじめてください。", statusCode: 410);
}
