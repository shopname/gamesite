using System.Numerics;
using System.Text;

namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 9×9 盤面の基本定義。ソルバー・難易度判定・ゲーム本体のすべてがこれを共有する。
///
/// マスは 0〜80 の通し番号で表す（左上が 0、右へ進み、行の終わりで次の行へ）。
///   row = cell / 9、col = cell % 9、box = (row / 3) * 3 + col / 3
///
/// 数字 1〜9 の「候補の集合」は 9 ビットの整数で表す（ビットマスク）。
///   数字 d のビット = 1 &lt;&lt; (d - 1)。例: 候補 {1, 4} = 0b000001001
/// 配列やリストより速く、和（|）・差（&amp; ~）・個数（PopCount）が 1 命令で計算できる。
/// </summary>
public static class SudokuGrid
{
    public const int Cells = 81;
    public const int AllDigits = 0x1FF; // 9 ビットすべて立っている = 候補 {1..9}

    /// <summary>
    /// 27 個の「ユニット」（同じ数字が入れない 9 マスの組）。
    /// 0〜8 = 行、9〜17 = 列、18〜26 = 3×3 ブロック。
    /// </summary>
    public static readonly int[][] Units;

    /// <summary>各マスの「ピア」（同じ行・列・ブロックにある自分以外の 20 マス）。</summary>
    public static readonly int[][] Peers;

    /// <summary>各マスが属する 3 つのユニット番号（行・列・ブロックの順）。</summary>
    public static readonly int[][] UnitsOfCell;

    static SudokuGrid()
    {
        Units = new int[27][];
        for (var i = 0; i < 9; i++)
        {
            Units[i] = Enumerable.Range(0, 9).Select(c => i * 9 + c).ToArray();          // 行
            Units[9 + i] = Enumerable.Range(0, 9).Select(r => r * 9 + i).ToArray();      // 列
            var br = i / 3 * 3;
            var bc = i % 3 * 3;
            Units[18 + i] = Enumerable.Range(0, 9).Select(k => (br + k / 3) * 9 + bc + k % 3).ToArray(); // ブロック
        }

        UnitsOfCell = new int[Cells][];
        Peers = new int[Cells][];
        for (var cell = 0; cell < Cells; cell++)
        {
            UnitsOfCell[cell] = [Row(cell), 9 + Col(cell), 18 + Box(cell)];
            Peers[cell] = UnitsOfCell[cell]
                .SelectMany(u => Units[u])
                .Where(p => p != cell)
                .Distinct()
                .ToArray();
        }
    }

    public static int Row(int cell) => cell / 9;
    public static int Col(int cell) => cell % 9;
    public static int Box(int cell) => cell / 27 * 3 + cell % 9 / 3;

    /// <summary>数字 d（1〜9）のビット。</summary>
    public static int Bit(int digit) => 1 << (digit - 1);

    /// <summary>ビットが 1 つだけ立っているマスクから数字を取り出す。</summary>
    public static int DigitOf(int singleBit) => BitOperations.TrailingZeroCount(singleBit) + 1;

    public static int Count(int mask) => BitOperations.PopCount((uint)mask);

    /// <summary>"..3.1..." のような 81 文字（空きは '.' か '0'）を配列にする。形式が違えば null。</summary>
    public static int[]? Parse(string? text)
    {
        if (text is null || text.Length != Cells) return null;
        var grid = new int[Cells];
        for (var i = 0; i < Cells; i++)
        {
            var ch = text[i];
            if (ch is '.' or '0') continue;
            if (ch is < '1' or > '9') return null;
            grid[i] = ch - '0';
        }
        return grid;
    }

    /// <summary>配列を 81 文字の文字列にする（空きは '0'）。ブラウザへの送信やセーブに使う。</summary>
    public static string Format(IReadOnlyList<int> grid)
    {
        var sb = new StringBuilder(Cells);
        foreach (var v in grid) sb.Append((char)('0' + v));
        return sb.ToString();
    }

    /// <summary>
    /// 初期数字どうしがルール違反（同じユニットに同じ数字）をしていないか。
    /// 解の探索より前に確かめておくと、壊れたデータで無駄な探索をしない。
    /// </summary>
    public static bool IsConsistent(int[] grid)
    {
        foreach (var unit in Units)
        {
            var seen = 0;
            foreach (var cell in unit)
            {
                var v = grid[cell];
                if (v == 0) continue;
                if ((seen & Bit(v)) != 0) return false;
                seen |= Bit(v);
            }
        }
        return true;
    }
}
