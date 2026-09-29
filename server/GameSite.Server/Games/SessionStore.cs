using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace GameSite.Server.Games;

/// <summary>サーバーのメモリ上で保持するプレイ中のゲーム。</summary>
public sealed class ActiveSession(string id, string gameId, Guid owner, GameSession game)
{
    public string Id { get; } = id;
    public string GameId { get; } = gameId;
    public Guid Owner { get; } = owner;
    public GameSession Game { get; } = game;

    /// <summary>ランキング登録は 1 セッションにつき 1 回まで。</summary>
    public bool ScoreSubmitted { get; set; }

    /// <summary>同じセッションへの同時操作を直列化するためのロック。</summary>
    public object Gate { get; } = new();
}

/// <summary>
/// プレイ中のゲームを一定時間（最後の操作から 30 分）保持する。
/// 同時保持数に上限を設け、大量に作られてもメモリを使い切らないようにしている。
/// </summary>
public sealed class SessionStore : IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 20_000 });

    public ActiveSession Create(string gameId, Guid owner, GameSession game)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var session = new ActiveSession(id, gameId, owner, game);
        _cache.Set(id, session, new MemoryCacheEntryOptions { SlidingExpiration = Idle, Size = 1 });
        return session;
    }

    /// <summary>セッションを取得する。別のプレイヤーや別のゲームのセッションは返さない。</summary>
    public ActiveSession? Get(string id, string gameId, Guid owner) =>
        _cache.TryGetValue(id, out ActiveSession? s) && s!.GameId == gameId && s.Owner == owner ? s : null;

    public void Dispose() => _cache.Dispose();
}
