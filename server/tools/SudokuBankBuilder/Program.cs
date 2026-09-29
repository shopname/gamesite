// 数独の問題バンクを作る・確かめるツール。
//
// ■ バンクを作る（17 個ヒントの一覧を 1 問ずつ判定して書き出す。数分かかる）
//   dotnet run -c Release -- build <17個ヒントの一覧.txt> ../../GameSite.Server/Games/Sudoku/Data/sudoku17.txt
//   元データ: Gordon Royle の 17 個ヒント一覧（49,158 問）。tdoku リポジトリの data.zip にある puzzles2_17_clue など。
//   出力は 1 行 1 問で「81 文字の盤面 + 空白 + 技法段階（2〜5）」。
//   段階 1（シングルだけで解ける）はどの難易度にも使わないので書き出さない。
//   各段階は最大 MaxPerLevel 問に間引く（サーバーに埋め込むファイルを小さく保つため）。
//
// ■ 出題を試す（各難易度で何問か作り、かかった時間と判定結果を表示する）
//   dotnet run -c Release -- bench ../../GameSite.Server/Games/Sudoku/Data/sudoku17.txt
using System.Diagnostics;
using GameSite.Server.Games.Sudoku;

const int MaxPerLevel = 4000;

return args switch
{
    ["build", var input, var output] => Build(input, output),
    ["bench", var bank] => Bench(bank),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("使い方: build <入力.txt> <出力.txt> | bench <バンク.txt>");
    return 1;
}

static int Build(string input, string output)
{
    var sw = Stopwatch.StartNew();
    var byLevel = new Dictionary<SudokuLevel, List<string>>();
    var invalid = 0;

    foreach (var raw in File.ReadLines(input))
    {
        var line = raw.Trim();
        if (line.Length < 81 || line.StartsWith('#')) continue;

        var puzzle = SudokuGrid.Parse(line[..81]);
        if (puzzle is null || SudokuSolver.CountSolutions(puzzle, 2) != 1) { invalid++; continue; }

        var level = SudokuRater.Rate(puzzle).Level;
        if (!byLevel.TryGetValue(level, out var list)) byLevel[level] = list = [];
        list.Add(SudokuGrid.Format(puzzle));
    }

    var lines = new List<string>
    {
        "# 数独の問題バンク（17 個ヒント・一意解）。server/tools/SudokuBankBuilder で生成。",
        "# 元データ: Gordon Royle による 17 個ヒント数独の一覧。書式: <81文字の盤面> <必要な技法段階 2〜5>",
    };
    Console.WriteLine($"判定完了: {sw.Elapsed.TotalSeconds:F1} 秒, 不正/非一意 {invalid} 問");
    foreach (var level in Enum.GetValues<SudokuLevel>())
    {
        var list = byLevel.GetValueOrDefault(level) ?? [];
        var kept = level == SudokuLevel.Singles ? [] : Thin(list, MaxPerLevel);
        lines.AddRange(kept.Select(p => $"{p} {(int)level}"));
        Console.WriteLine($"  {level,-14} {list.Count,6} 問 → {kept.Count,5} 問を収録");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllLines(output, lines);
    Console.WriteLine($"書き出し: {output}");
    return 0;
}

// 等間隔に間引く（毎回同じ結果になるよう、乱数は使わない）
static List<string> Thin(List<string> list, int max) =>
    list.Count <= max ? list : Enumerable.Range(0, max).Select(i => list[(int)((long)i * list.Count / max)]).ToList();

static int Bench(string bankPath)
{
    var bank = new SudokuPuzzleBank(File.ReadLines(bankPath));
    Console.WriteLine($"バンク: {bank.Count} 問");
    var rng = new SudokuRandom(12345);
    foreach (var diff in SudokuDifficulty.All)
    {
        var sw = Stopwatch.StartNew();
        var levels = new int[6];
        const int n = 30;
        for (var i = 0; i < n; i++)
        {
            var p = bank.Create(diff, rng);
            if (p.Clues != diff.Clues) throw new Exception($"ヒント数が違う: {p.Clues}");
            levels[(int)p.Rating.Level]++;
        }
        Console.WriteLine($"{diff.Label,-8} 平均 {sw.Elapsed.TotalMilliseconds / n,7:F1} ms/問  段階: " +
            string.Join(" ", Enumerable.Range(1, 5).Select(l => $"{(SudokuLevel)l}={levels[l]}")));
    }
    return 0;
}
