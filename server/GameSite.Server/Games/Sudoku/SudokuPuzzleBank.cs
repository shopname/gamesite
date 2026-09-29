using System.Reflection;

namespace GameSite.Server.Games.Sudoku;

/// <summary>出題する 1 問。Solution はサーバーの中だけで使い、ブラウザには送らない。</summary>
public sealed record SudokuPuzzle(int[] Givens, int[] Solution, SudokuRating Rating)
{
    public int Clues => Givens.Count(v => v != 0);
}

/// <summary>
/// 問題の出題係。
///
/// ■ なぜ「問題バンク」を使うのか
///   17 個ヒントで一意解の問題は、全盤面を調べ尽くした研究（McGuire ほか, arXiv:1201.0749）で
///   約 5 万種類しか存在しないことが分かっている。ランダムに数字を抜いて作る方法では、
///   ふつう 22〜25 個あたりで一意解が保てなくなり、17 個にはまず届かない。
///   そこで既知の 17 個ヒント問題（Gordon Royle の一覧、49,158 問）を難易度判定した
///   Data/sudoku17.txt を同梱し、そこから出題する。
///
/// ■ 1 問の作り方
///   1. 目標の難易度に使えそうな問題をバンクから選ぶ
///   2. 数字・行・列の入れ替え（SudokuTransform）で、見た目の違う盤面にする
///   3. 19 個・21 個の難易度なら、正解から数マスを選んで初期数字として足す
///      （ヒントを足しても解が増えることはないので、一意解のまま）
///   4. SudokuRater で必要な技法を判定し、難易度の範囲に入らなければ 1 からやり直す
///   5. 最後に SudokuSolver で「解が 1 つだけ」であることを必ず確かめる
/// </summary>
public sealed class SudokuPuzzleBank
{
    private const int MaxAttempts = 400;

    // Level ごとの問題（81 マスの配列）
    private readonly Dictionary<SudokuLevel, int[][]> _byLevel;

    public SudokuPuzzleBank(IEnumerable<string> lines)
    {
        var groups = new Dictionary<SudokuLevel, List<int[]>>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 83 || line.StartsWith('#')) continue;
            var grid = SudokuGrid.Parse(line[..81]);
            if (grid is null || !int.TryParse(line[82..], out var level)) continue;
            var key = (SudokuLevel)level;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(grid);
        }
        _byLevel = groups.ToDictionary(g => g.Key, g => g.Value.ToArray());
        Count = _byLevel.Values.Sum(v => v.Length);
    }

    public int Count { get; }

    /// <summary>サーバーに埋め込んだ Data/sudoku17.txt を読み込む。</summary>
    public static SudokuPuzzleBank LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("sudoku17.txt", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return new SudokuPuzzleBank(lines);
    }

    /// <summary>指定の難易度の問題を 1 つ作る。同じ rng（同じ種）なら必ず同じ問題になる。</summary>
    public SudokuPuzzle Create(SudokuDifficulty difficulty, SudokuRandom rng)
    {
        // 元の問題（17 個）は、目標の最もやさしい段階以上のものだけを使う。
        // ヒントを足すと問題はやさしくなる方向にしか動かないので、それより下から始めても届かない。
        var pools = _byLevel
            .Where(kv => kv.Key >= difficulty.MinLevel && kv.Key <= SudokuLevel.Trial)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToArray();
        if (pools.Length == 0) throw new InvalidOperationException("問題バンクに使える問題がありません。");

        SudokuPuzzle? fallback = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // 段階ごとの問題数の偏りに引きずられないよう、まず段階を選び、その中から 1 問選ぶ
            var pool = pools[rng.Next(pools.Length)];
            var givens = SudokuTransform.Apply(pool[rng.Next(pool.Length)], rng);

            if (!SudokuSolver.TrySolveUnique(givens, out var solution)) continue; // バンクが壊れていた場合の保険
            AddClues(givens, solution, difficulty.Clues, rng);

            var rating = SudokuRater.Rate(givens);
            var puzzle = new SudokuPuzzle(givens, solution, rating);
            if (difficulty.Accepts(rating.Level)) return Verified(puzzle);

            // 範囲外でも、目標に近い（難しい側に外れた）ものを予備として覚えておく
            if (rating.Level >= difficulty.MinLevel && (fallback is null || rating.Level < fallback.Rating.Level))
                fallback = puzzle;
        }

        // 規定回数で見つからなければ予備を使う（実測ではまず起きない）
        return Verified(fallback ?? throw new InvalidOperationException("条件に合う問題を作れませんでした。"));
    }

    /// <summary>正解からランダムに空きマスを選び、初期数字が target 個になるまで足す。</summary>
    private static void AddClues(int[] givens, int[] solution, int target, SudokuRandom rng)
    {
        var empty = Enumerable.Range(0, SudokuGrid.Cells).Where(c => givens[c] == 0).ToArray();
        rng.Shuffle(empty);
        var need = target - (SudokuGrid.Cells - empty.Length);
        for (var i = 0; i < need && i < empty.Length; i++) givens[empty[i]] = solution[empty[i]];
    }

    /// <summary>仕様「すべての問題について解が一意であることを検証する」。出題の直前に必ず通す。</summary>
    private static SudokuPuzzle Verified(SudokuPuzzle p) =>
        SudokuSolver.CountSolutions(p.Givens, 2) == 1
            ? p
            : throw new InvalidOperationException("一意解でない問題が作られました。");
}
