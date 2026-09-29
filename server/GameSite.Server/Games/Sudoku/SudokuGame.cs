using System.Collections.Concurrent;
using System.Text.Json;

namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 数独のロジック（game.json の id = "sudoku"）。
///
/// サーバー（C#）とブラウザ（JS）の役割分担:
///   サーバー … 出題・正解の保持・入力の正誤判定・ミス回数・経過時間・スコア計算
///   ブラウザ … 盤面の表示・候補メモ・「元に戻す」の履歴・アニメーション
/// 候補メモは正誤に関係せず、ランキングにも影響しないので、ブラウザだけで管理する（通信を減らせる）。
///
/// ブラウザから届くオプション:
///   { "mode": "hard" | "very_hard" | "extreme" | "daily" }  … 新しい問題
///   { "restore": "&lt;セーブ&gt;" }                             … 中断したゲームの復元
/// </summary>
public sealed class SudokuGameLogic : IGameLogic
{
    public string GameId => "sudoku";

    // 問題バンクは最初の出題時に 1 回だけ読み込む（起動を遅くしないため）
    private readonly Lazy<SudokuPuzzleBank> _bank = new(SudokuPuzzleBank.LoadEmbedded);
    private readonly ConcurrentDictionary<DateOnly, Lazy<SudokuPuzzle>> _daily = new();
    private readonly SudokuSaveSigner _signer;
    private readonly SudokuGuard _guard = new();

    public SudokuGameLogic(IConfiguration config, IHostEnvironment env, ILogger<SudokuGameLogic> logger)
    {
        _signer = SudokuSaveSigner.Create(config, env, logger);
    }

    public GameSession CreateSession(JsonElement options, GameContext context)
    {
        if (ReadString(options, "restore") is { } token) return Restore(token, context);

        var mode = ReadString(options, "mode");
        if (mode == SudokuDaily.ModeId)
        {
            var date = SudokuDaily.Today(context.Time);
            var puzzle = DailyPuzzle(date);
            // デイリーのランキング対象は、各プレイヤーのその日最初の挑戦だけ（答えを覚えての再挑戦を防ぐ）
            var first = _guard.TryStartDaily(context.Player, date);
            return NewSession(new SudokuStart(puzzle, SudokuDifficulty.Extreme, date,
                Ranked: first, UnrankedReason: first ? null : "デイリー問題の 2 回目以降の挑戦"), context);
        }

        var difficulty = SudokuDifficulty.From(mode);
        var rng = new SudokuRandom((ulong)context.Random.NextInt64());
        return NewSession(new SudokuStart(_bank.Value.Create(difficulty, rng), difficulty, null), context);
    }

    /// <summary>その日のデイリー問題。日付から作った種だけで決まるので、全員に同じ盤面が配られる。</summary>
    private SudokuPuzzle DailyPuzzle(DateOnly date)
    {
        // 古い日付を捨てる（前日分は日付をまたいで遊んでいる人のために残す）
        foreach (var old in _daily.Keys.Where(d => d < date.AddDays(-1))) _daily.TryRemove(old, out _);

        // Lazy で包むと、同時に複数のリクエストが来ても生成は 1 回だけになる
        return _daily.GetOrAdd(date, d => new Lazy<SudokuPuzzle>(() =>
            _bank.Value.Create(SudokuDifficulty.Extreme, new SudokuRandom(SudokuDaily.Seed(d))))).Value;
    }

    /// <summary>セーブから復元する。署名が合わない・中身がおかしい場合は「復元失敗」のセッションを返す。</summary>
    private GameSession Restore(string token, GameContext context)
    {
        var save = _signer.Verify(token);
        var givens = SudokuGrid.Parse(save?.Givens);
        var placed = SudokuGrid.Parse(save?.Placed);
        if (save is null || givens is null || placed is null || !SudokuSolver.TrySolveUnique(givens, out var solution))
            return new SudokuRestoreFailedSession();

        for (var c = 0; c < SudokuGrid.Cells; c++)
        {
            // 初期数字のマスに入力がある、または正解と違う数字がある → 壊れたセーブ
            if (placed[c] != 0 && (givens[c] != 0 || placed[c] != solution[c])) return new SudokuRestoreFailedSession();
        }

        DateOnly? daily = DateOnly.TryParse(save.Daily, out var d) ? d : null;
        var difficulty = daily is null ? SudokuDifficulty.From(save.Difficulty) : SudokuDifficulty.Extreme;

        // ランキング対象を保つ条件:
        //   - 一時停止中に作られたセーブであること（盤面を見ながら時計の外で考える抜け道を防ぐ）
        //   - そのゲームの最新のセーブであること（ミスの前の古いセーブに巻き戻す抜け道を防ぐ）
        var ranked = save.Ranked;
        var reason = save.UnrankedReason;
        if (ranked && !save.Paused) (ranked, reason) = (false, "一時停止せずに中断したプレイ");
        if (ranked && _guard.IsStale(save.Nonce, save.Seq)) (ranked, reason) = (false, "古いセーブからの復元");

        var puzzle = new SudokuPuzzle(givens, solution, SudokuRater.Rate(givens));
        return NewSession(new SudokuStart(puzzle, difficulty, daily, ranked, reason,
            placed, save.Mistakes, save.ElapsedMs, save.Nonce, save.Seq), context);
    }

    private SudokuSession NewSession(SudokuStart start, GameContext context) =>
        new(start, context.Time, _signer, _guard);

    private static string? ReadString(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>セッション開始時の材料（新しい問題でも、セーブからの復元でも同じ形）。</summary>
public sealed record SudokuStart(
    SudokuPuzzle Puzzle,
    SudokuDifficulty Difficulty,
    DateOnly? Daily,
    bool Ranked = true,
    string? UnrankedReason = null,
    int[]? Placed = null,
    int Mistakes = 0,
    long ElapsedMs = 0,
    string? Nonce = null,
    int Seq = 0);

/// <summary>
/// ブラウザから届く操作。
///   { "type": "place",   "cell": 40, "digit": 7 } … 数字を確定する（その場で正誤判定）
///   { "type": "unplace", "cell": 40 }             … 確定した数字を取り消す（「元に戻す」）
///   { "type": "pause" } / { "type": "resume" }    … 一時停止 / 再開（時計を止める・動かす）
///   { "type": "reset" }                           … 同じ問題を最初から
///   { "type": "retire" }                          … リタイア（記録なしで終了）
///   { "type": "sync" }                            … 何もせず最新の状態を返す（生存確認）
/// </summary>
public sealed record SudokuAction(string Type, int Cell = -1, int Digit = 0);

public sealed class SudokuSession : GameSession<SudokuAction>
{
    public const int MaxMistakes = 3;

    /// <summary>これより速いクリアは人間の入力として不自然なので、ランキングに載せない。</summary>
    private static readonly TimeSpan MinRankedTime = TimeSpan.FromSeconds(60);

    /// <summary>時間ボーナスが 0 になるまでの秒数（90 分）。</summary>
    private const int TimeBonusSeconds = 90 * 60;
    private const int MistakePenalty = 500;

    private readonly TimeProvider _time;
    private readonly SudokuSaveSigner _signer;
    private readonly SudokuGuard _guard;
    private readonly SudokuDifficulty _difficulty;
    private readonly DateOnly? _daily;
    private readonly SudokuRating _rating;
    private readonly int[] _givens;
    private readonly int[] _solution;   // 正解（秘密。ブラウザには送らない）
    private readonly int[] _cells;      // 今の盤面（初期数字 + 正しく確定した数字）
    private readonly int _emptyAtStart;
    private readonly string _nonce;
    private readonly int _generation;

    private int _mistakes;
    private long _accumulatedMs;        // 一時停止までに経過した時間の合計
    private DateTimeOffset? _runningSince; // 時計が動いているなら、その開始時刻（null = 一時停止中）
    private bool _ranked;
    private string? _unrankedReason;
    private string? _result;            // "clear" | "gameover" | "retired"
    private int _seq;

    public SudokuSession(SudokuStart start, TimeProvider time, SudokuSaveSigner signer, SudokuGuard guard)
    {
        _time = time;
        _signer = signer;
        _guard = guard;
        _difficulty = start.Difficulty;
        _daily = start.Daily;
        _rating = start.Puzzle.Rating;
        _givens = start.Puzzle.Givens;
        _solution = start.Puzzle.Solution;
        _emptyAtStart = _givens.Count(v => v == 0);

        _cells = (int[])_givens.Clone();
        if (start.Placed is { } placed)
            for (var c = 0; c < SudokuGrid.Cells; c++)
                if (placed[c] != 0) _cells[c] = placed[c];

        _mistakes = start.Mistakes;
        _accumulatedMs = start.ElapsedMs;
        _runningSince = time.GetUtcNow();
        _ranked = start.Ranked;
        _unrankedReason = start.UnrankedReason;
        _nonce = start.Nonce ?? Guid.NewGuid().ToString("N");
        _seq = start.Seq;
        _generation = guard.Claim(_nonce); // 同じゲームの古いセッションは、これ以降操作できなくなる

        // ランキングは難易度ごと。デイリーは日付ごとに分け、その日の同じ盤面だけで比べる
        Mode = _daily is { } d ? SudokuDaily.RankingMode(d) : _difficulty.Id;
        Touch();
    }

    private bool Paused => _runningSince is null;

    private long ElapsedMs => _accumulatedMs + (_runningSince is { } s ? (long)(_time.GetUtcNow() - s).TotalMilliseconds : 0);

    /// <summary>数字 d の残数 = 9 − 盤面にある d の個数（初期数字 + 正しく確定した数字）。</summary>
    private int Remaining(int digit) => 9 - _cells.Count(v => v == digit);

    private int Filled => _cells.Count(v => v != 0) - (SudokuGrid.Cells - _emptyAtStart);

    protected override ActionOutcome Handle(SudokuAction action) => !_guard.IsCurrent(_nonce, _generation)
        ? ActionOutcome.Invalid("このゲームは別の画面で再開されました。")
        : action.Type switch
    {
        "place" => Place(action.Cell, action.Digit),
        "unplace" => Unplace(action.Cell),
        "pause" => Pause(),
        "resume" => Resume(),
        "reset" => Reset(),
        "retire" => Retire(),
        "sync" => ActionOutcome.Success(),
        _ => ActionOutcome.Invalid("不明な操作です。"),
    };

    private ActionOutcome Place(int cell, int digit)
    {
        if (cell is < 0 or >= SudokuGrid.Cells || digit is < 1 or > 9) return ActionOutcome.Invalid("マスか数字が正しくありません。");
        if (Paused) return ActionOutcome.Invalid("一時停止中は入力できません。");
        if (_givens[cell] != 0) return ActionOutcome.Invalid("初期数字は変更できません。");
        if (_cells[cell] != 0) return ActionOutcome.Invalid("そのマスはすでに確定しています。");
        if (Remaining(digit) == 0) return ActionOutcome.Invalid($"{digit} はすべて埋まっています。");

        // 誤答: ミスを 1 回数え、盤面には残さない
        if (_solution[cell] != digit)
        {
            _mistakes++;
            Touch();
            var mistake = new { type = "mistake", cell, digit, mistakes = _mistakes, maxMistakes = MaxMistakes };
            if (_mistakes < MaxMistakes) return ActionOutcome.Success(mistake);

            Finish("gameover");
            return ActionOutcome.Success(mistake, new { type = "gameover", elapsedMs = ElapsedMs, mistakes = _mistakes });
        }

        // 正解: 確定する
        _cells[cell] = digit;
        Touch();

        // この入力で埋まりきった行・列・ブロック（アニメーション用）
        var units = SudokuGrid.UnitsOfCell[cell]
            .Where(u => Array.TrueForAll(SudokuGrid.Units[u], c => _cells[c] != 0))
            .Select(u => new { kind = u < 9 ? "row" : u < 18 ? "col" : "box", index = u % 9 })
            .ToArray();
        var placed = new { type = "placed", cell, digit, units, digitDone = Remaining(digit) == 0 };

        if (Filled < _emptyAtStart) return ActionOutcome.Success(placed);

        Finish("clear");
        return ActionOutcome.Success(placed, new
        {
            type = "clear",
            score = FinalScore,
            points = Points(),
            ranked = FinalScore is not null,
            unrankedReason = _unrankedReason,
            elapsedMs = ElapsedMs,
            mistakes = _mistakes,
        });
    }

    /// <summary>「元に戻す」: プレイヤーが確定した数字を取り消す。ミス回数は戻らない。</summary>
    private ActionOutcome Unplace(int cell)
    {
        if (cell is < 0 or >= SudokuGrid.Cells) return ActionOutcome.Invalid("マスが正しくありません。");
        if (Paused) return ActionOutcome.Invalid("一時停止中は操作できません。");
        if (_givens[cell] != 0 || _cells[cell] == 0) return ActionOutcome.Invalid("取り消せる入力がありません。");

        var digit = _cells[cell];
        _cells[cell] = 0;
        Touch();
        return ActionOutcome.Success(new { type = "unplaced", cell, digit });
    }

    private ActionOutcome Pause()
    {
        if (_runningSince is { } since)
        {
            _accumulatedMs += (long)(_time.GetUtcNow() - since).TotalMilliseconds;
            _runningSince = null;
            Touch();
        }
        return ActionOutcome.Success(new { type = "paused" });
    }

    private ActionOutcome Resume()
    {
        if (Paused)
        {
            _runningSince = _time.GetUtcNow();
            Touch();
        }
        return ActionOutcome.Success(new { type = "resumed" });
    }

    /// <summary>
    /// リセット: 同じ問題を最初から。入力・ミス・経過時間を初期化する（候補メモはブラウザ側で消す）。
    /// 一度でも正解を確かめたマスを覚えたままやり直せるので、このプレイはランキング対象外にする。
    /// </summary>
    private ActionOutcome Reset()
    {
        Array.Copy(_givens, _cells, SudokuGrid.Cells);
        _mistakes = 0;
        _accumulatedMs = 0;
        _runningSince = _time.GetUtcNow();
        _ranked = false;
        _unrankedReason = "リセットしたプレイ";
        Touch();
        return ActionOutcome.Success(new { type = "reset" });
    }

    private ActionOutcome Retire()
    {
        Finish("retired");
        return ActionOutcome.Success(new { type = "retired" });
    }

    private void Finish(string result)
    {
        if (_runningSince is { } since) _accumulatedMs += (long)(_time.GetUtcNow() - since).TotalMilliseconds;
        _runningSince = null;
        _result = result;
        IsFinished = true;

        if (result != "clear") return; // ゲームオーバー・リタイアはスコアなし（ランキングに載らない）
        if (_ranked && _accumulatedMs < MinRankedTime.TotalMilliseconds)
            (_ranked, _unrankedReason) = (false, "記録が速すぎるため");
        FinalScore = _ranked ? Points() : null; // FinalScore が null ならランキング登録できない
    }

    /// <summary>
    /// スコア = 基礎点（難易度） + 時間ボーナス − ミス × 500。
    ///   基礎点: 難しい 3000 / とても難しい 4000 / 極めて難しい・デイリー 5000
    ///   時間ボーナス: 90 分から 1 秒速いごとに +1（90 分以上は 0）
    /// ランキングは難易度（デイリーは日付）ごとなので、同じ区分の中では「速く・ミスなく」が上位になる。
    /// 同点は同順位（既存のランキング実装どおり。表示は先に記録した人が上）。
    /// </summary>
    private int Points()
    {
        var seconds = (int)(_accumulatedMs / 1000);
        var timeBonus = Math.Max(0, TimeBonusSeconds - seconds);
        return Math.Max(1, _difficulty.BaseScore + timeBonus - _mistakes * MistakePenalty);
    }

    /// <summary>状態が変わったらセーブ番号を進め、最新の番号をサーバー側にも記録する。</summary>
    private void Touch()
    {
        _seq++;
        _guard.RecordSeq(_nonce, _seq);
    }

    private static string TechniqueLabel(SudokuLevel level) => level switch
    {
        SudokuLevel.Singles => "シングル",
        SudokuLevel.Intersections => "ロックされた候補",
        SudokuLevel.Subsets => "ペア・トリプル",
        SudokuLevel.Advanced => "上級技法（X-Wing・XY-Wing など）",
        _ => "仮置き（試行錯誤）",
    };

    public override object GetView()
    {
        var elapsed = ElapsedMs;
        return new
        {
            mode = Mode,
            difficulty = _difficulty.Id,
            label = _daily is null ? _difficulty.Label : "デイリー",
            daily = _daily?.ToString("yyyy-MM-dd"),
            givens = SudokuGrid.Format(_givens),
            cells = SudokuGrid.Format(_cells),
            clues = SudokuGrid.Cells - _emptyAtStart,
            mistakes = _mistakes,
            maxMistakes = MaxMistakes,
            emptyAtStart = _emptyAtStart,
            filled = Filled,
            // 進捗率 = 正しく埋めたマス ÷ 開始時の空きマス。切り捨てなので 100% は完成時だけ
            progress = _emptyAtStart == 0 ? 100 : Filled * 100 / _emptyAtStart,
            elapsedMs = elapsed,
            paused = Paused,
            finished = IsFinished,
            result = _result,
            score = FinalScore,
            points = _result == "clear" ? Points() : (int?)null,
            ranked = _ranked,
            unrankedReason = _unrankedReason,
            // 解き終わってから、その問題に必要だった技法を見せる（プレイ中に見せるとヒントになる）
            technique = IsFinished ? TechniqueLabel(_rating.Level) : null,
            // 中断に備えたセーブ（終わったゲームには不要）
            save = IsFinished ? null : _signer.Sign(new SudokuSave(
                SudokuSaveSigner.Version, _nonce, _seq, _difficulty.Id, _daily?.ToString("yyyy-MM-dd"),
                SudokuGrid.Format(_givens), PlacedString(), _mistakes, elapsed, Paused, _ranked, _unrankedReason)),
        };
    }

    private string PlacedString()
    {
        var placed = new int[SudokuGrid.Cells];
        for (var c = 0; c < SudokuGrid.Cells; c++)
            if (_givens[c] == 0) placed[c] = _cells[c];
        return SudokuGrid.Format(placed);
    }
}

/// <summary>セーブが壊れていた・改ざんされていたときに返す、何もできないセッション。</summary>
public sealed class SudokuRestoreFailedSession : GameSession<SudokuAction>
{
    public SudokuRestoreFailedSession() => IsFinished = true;

    protected override ActionOutcome Handle(SudokuAction action) => ActionOutcome.Invalid("このゲームは復元できませんでした。");

    public override object GetView() => new { restoreFailed = true, finished = true };
}
