namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 難易度の定義。ここを変えれば、出題の条件とスコアの基礎点がまとめて変わる。
///
/// 判定基準（仕様「解法に基づく難易度判定」の具体化）:
///   難しい       … 初期数字 21 個。ロックされた候補〜ペア/トリプルが必要（シングルだけでは解けない）
///   とても難しい … 初期数字 19 個。ペア/トリプル〜上級技法（X-Wing・XY-Wing など）が必要
///   極めて難しい … 初期数字 17 個。上級技法が必要、または仮置き・試行錯誤が必要
/// 必要な技法は SudokuRater が実際に解いて判定する（SudokuLevel を参照）。
/// </summary>
/// <param name="Id">game.json の modes[].id と同じ文字列。ランキングの区分にもなる。</param>
/// <param name="Clues">初期数字の個数。</param>
/// <param name="MinLevel">出題してよい最もやさしい技法段階。</param>
/// <param name="MaxLevel">出題してよい最も難しい技法段階。</param>
/// <param name="BaseScore">クリア時の基礎点。</param>
public sealed record SudokuDifficulty(
    string Id, string Label, int Clues, SudokuLevel MinLevel, SudokuLevel MaxLevel, int BaseScore)
{
    public static readonly SudokuDifficulty Hard =
        new("hard", "難しい", 21, SudokuLevel.Intersections, SudokuLevel.Subsets, 3000);

    public static readonly SudokuDifficulty VeryHard =
        new("very_hard", "とても難しい", 19, SudokuLevel.Subsets, SudokuLevel.Advanced, 4000);

    public static readonly SudokuDifficulty Extreme =
        new("extreme", "極めて難しい", 17, SudokuLevel.Advanced, SudokuLevel.Trial, 5000);

    public static readonly IReadOnlyList<SudokuDifficulty> All = [Hard, VeryHard, Extreme];

    /// <summary>不明な値は「とても難しい」（game.json の default と同じ）にそろえる。</summary>
    public static SudokuDifficulty From(string? id) => All.FirstOrDefault(d => d.Id == id) ?? VeryHard;

    public bool Accepts(SudokuLevel level) => level >= MinLevel && level <= MaxLevel;
}

/// <summary>
/// デイリー問題（全員が同じ盤面を解く日替わり問題）の日付まわり。
///
/// 切り替え: 日本時間（UTC+9）の 0:00。日本は夏時間がないので、固定の +9 時間で正確に計算できる。
/// 配信方法: 日付から乱数の種を作り（SudokuRandom.SeedFrom）、その種だけで問題を選ぶ。
///   サーバーに問題を保存しなくても、同じ日付なら誰がいつ開いても同じ盤面になる。
/// ランキング: モードを "daily-2026-09-30" のように日付入りにして、その日の盤面だけで比べる。
/// </summary>
public static class SudokuDaily
{
    public const string ModeId = "daily";
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);

    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Offset).DateTime);

    public static string RankingMode(DateOnly date) => $"{ModeId}-{date:yyyy-MM-dd}";

    public static ulong Seed(DateOnly date) => SudokuRandom.SeedFrom($"gamesite:sudoku:daily:{date:yyyy-MM-dd}");
}
