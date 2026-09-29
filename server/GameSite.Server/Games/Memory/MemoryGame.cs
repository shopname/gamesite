using System.Text.Json;

namespace GameSite.Server.Games.Memory;

/// <summary>
/// 神経衰弱のロジック。
/// カードの並びはサーバーだけが知っていて、ブラウザにはめくられたカードの絵柄しか送らない。
/// そのためブラウザの開発者ツールを見ても答えは分からず、得点もサーバーが計算する。
/// </summary>
public sealed class MemoryGameLogic : IGameLogic
{
    public string GameId => "memory";

    public GameSession CreateSession(JsonElement options, GameContext context)
    {
        var mode = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        return new MemorySession(MemoryDifficulty.From(mode), context);
    }
}

public sealed record MemoryDifficulty(string Id, int Pairs, int Columns)
{
    public static readonly MemoryDifficulty Easy = new("easy", 6, 3);
    public static readonly MemoryDifficulty Normal = new("normal", 8, 4);
    public static readonly MemoryDifficulty Hard = new("hard", 10, 4);

    public static MemoryDifficulty From(string? id) => id switch
    {
        "easy" => Easy,
        "hard" => Hard,
        _ => Normal,
    };
}

/// <summary>ブラウザから送られる操作。{ "type": "flip", "index": 3 }</summary>
public sealed record MemoryAction(string Type, int Index);

public sealed class MemorySession : GameSession<MemoryAction>
{
    private static readonly string[] Faces = ["🍎", "🐶", "🚗", "⭐", "🎈", "🐟", "🌸", "🍩", "⚽", "🎵", "🐢", "🍉"];

    private enum CardState { Hidden, Up, Matched }

    private readonly MemoryDifficulty _difficulty;
    private readonly TimeProvider _time;
    private readonly int[] _faces;          // 各位置の絵柄番号（秘密）
    private readonly CardState[] _states;
    private int? _firstIndex;
    private int _moves;
    private int _matchedPairs;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public MemorySession(MemoryDifficulty difficulty, GameContext context)
    {
        _difficulty = difficulty;
        _time = context.Time;
        Mode = difficulty.Id;

        _faces = Enumerable.Range(0, difficulty.Pairs).SelectMany(i => new[] { i, i }).ToArray();
        context.Random.Shuffle(_faces);
        _states = new CardState[_faces.Length];
    }

    protected override ActionOutcome Handle(MemoryAction action)
    {
        if (action.Type != "flip") return ActionOutcome.Invalid("不明な操作です。");
        var i = action.Index;
        if (i < 0 || i >= _faces.Length) return ActionOutcome.Invalid("カードの位置が正しくありません。");
        if (_states[i] != CardState.Hidden) return ActionOutcome.Invalid("そのカードはめくれません。");

        _startedAt ??= _time.GetUtcNow();
        var flip = new { type = "flip", index = i, face = Faces[_faces[i]] };

        // 1 枚目
        if (_firstIndex is not int first)
        {
            _states[i] = CardState.Up;
            _firstIndex = i;
            return ActionOutcome.Success(flip);
        }

        // 2 枚目
        _moves++;
        _firstIndex = null;

        if (_faces[first] != _faces[i])
        {
            _states[first] = CardState.Hidden;
            _states[i] = CardState.Hidden;
            return ActionOutcome.Success(flip, new { type = "mismatch", indices = new[] { first, i } });
        }

        _states[first] = CardState.Matched;
        _states[i] = CardState.Matched;
        _matchedPairs++;

        if (_matchedPairs < _difficulty.Pairs)
            return ActionOutcome.Success(flip, new { type = "match", indices = new[] { first, i } });

        _finishedAt = _time.GetUtcNow();
        IsFinished = true;
        FinalScore = CalculateScore();
        return ActionOutcome.Success(flip,
            new { type = "match", indices = new[] { first, i } },
            new { type = "finish", score = FinalScore, moves = _moves, elapsedMs = ElapsedMs });
    }

    /// <summary>
    /// 得点 = ペア数×100 ＋ 手数ボーナス ＋ 時間ボーナス。
    /// 最小手数（ペア数と同じ）に近いほど、早いほど高得点。
    /// </summary>
    private int CalculateScore()
    {
        var pairs = _difficulty.Pairs;
        var misses = _moves - pairs;
        var seconds = (int)(ElapsedMs / 1000);
        var moveBonus = Math.Max(0, pairs * 60 - misses * 20);
        var timeBonus = Math.Max(0, pairs * 30 - seconds * 2);
        return pairs * 100 + moveBonus + timeBonus;
    }

    private long ElapsedMs => _startedAt is { } s ? (long)((_finishedAt ?? _time.GetUtcNow()) - s).TotalMilliseconds : 0;

    public override object GetView() => new
    {
        mode = Mode,
        columns = _difficulty.Columns,
        pairs = _difficulty.Pairs,
        matchedPairs = _matchedPairs,
        moves = _moves,
        elapsedMs = ElapsedMs,
        started = _startedAt is not null,
        finished = IsFinished,
        score = FinalScore,
        cards = _states.Select((s, i) => new
        {
            state = s switch { CardState.Up => "up", CardState.Matched => "matched", _ => "hidden" },
            face = s == CardState.Hidden ? null : Faces[_faces[i]], // 裏向きのカードの絵柄は送らない
        }),
    };
}
