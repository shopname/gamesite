namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 総当たり（バックトラッキング）で数独を解くソルバー。
/// 役割は 2 つ:
///   1. 答え（正解の盤面）を求める … 入力の正誤判定に使う
///   2. 解がいくつあるか数える     … 「解が 1 つだけ（一意解）」の検証に使う
///
/// 人間の解き方は真似せず、速さだけを重視している（人間らしい解き方は SudokuRater が担当）。
/// 速くするための工夫:
///   - 行・列・ブロックごとに「使用済みの数字」をビットマスクで持ち、候補を 1 回の演算で求める
///   - 候補がいちばん少ないマスから埋める（MRV: Minimum Remaining Values）。
///     候補 1 個のマスは実質「確定」なので分岐せず、行き詰まりも早く見つかる
/// 17 個ヒントの難問でも、1 問あたり数ミリ秒で解き終わる。
/// </summary>
public static class SudokuSolver
{
    /// <summary>
    /// 解の数を limit 個まで数える。一意性の確認なら limit = 2 で十分
    /// （2 個目が見つかった時点で「一意ではない」と分かるので、それ以上探さない）。
    /// </summary>
    /// <param name="firstSolution">null でなければ、最初に見つけた解をここに書き込む。</param>
    public static int CountSolutions(int[] puzzle, int limit, int[]? firstSolution = null)
    {
        if (puzzle.Length != SudokuGrid.Cells || !SudokuGrid.IsConsistent(puzzle)) return 0;

        var state = new SearchState((int[])puzzle.Clone(), limit, firstSolution);
        for (var cell = 0; cell < SudokuGrid.Cells; cell++)
        {
            var v = state.Grid[cell];
            if (v != 0) state.Mark(cell, SudokuGrid.Bit(v));
        }
        state.Search();
        return state.Found;
    }

    /// <summary>解がちょうど 1 つなら true を返し、その解を solution に入れる。</summary>
    public static bool TrySolveUnique(int[] puzzle, out int[] solution)
    {
        solution = new int[SudokuGrid.Cells];
        return CountSolutions(puzzle, 2, solution) == 1;
    }

    private sealed class SearchState(int[] grid, int limit, int[]? firstSolution)
    {
        public readonly int[] Grid = grid;
        // Rows[r] = 行 r ですでに使われている数字のビット集合（Cols, Boxes も同様）
        private readonly int[] _rows = new int[9], _cols = new int[9], _boxes = new int[9];
        public int Found;

        public void Mark(int cell, int bit)
        {
            _rows[SudokuGrid.Row(cell)] |= bit;
            _cols[SudokuGrid.Col(cell)] |= bit;
            _boxes[SudokuGrid.Box(cell)] |= bit;
        }

        private void Unmark(int cell, int bit)
        {
            _rows[SudokuGrid.Row(cell)] &= ~bit;
            _cols[SudokuGrid.Col(cell)] &= ~bit;
            _boxes[SudokuGrid.Box(cell)] &= ~bit;
        }

        private int Candidates(int cell) =>
            SudokuGrid.AllDigits & ~(_rows[SudokuGrid.Row(cell)] | _cols[SudokuGrid.Col(cell)] | _boxes[SudokuGrid.Box(cell)]);

        /// <summary>戻り値 true = 必要な数だけ解が見つかったので探索を打ち切る。</summary>
        public bool Search()
        {
            // 候補がいちばん少ない空きマスを探す（MRV）
            int best = -1, bestCands = 0, bestCount = 10;
            for (var cell = 0; cell < SudokuGrid.Cells; cell++)
            {
                if (Grid[cell] != 0) continue;
                var cands = Candidates(cell);
                var n = SudokuGrid.Count(cands);
                if (n == 0) return false;          // 入る数字がないマス → この枝は矛盾
                if (n < bestCount)
                {
                    best = cell; bestCands = cands; bestCount = n;
                    if (n == 1) break;             // これ以上少なくはならない
                }
            }

            if (best < 0)
            {
                // 空きマスがない = 解を 1 つ発見
                if (Found == 0 && firstSolution is not null) Array.Copy(Grid, firstSolution, SudokuGrid.Cells);
                Found++;
                return Found >= limit;
            }

            // 候補を 1 つずつ仮に置いて、再帰的に続きを探す
            for (var rest = bestCands; rest != 0; rest &= rest - 1)
            {
                var bit = rest & -rest;            // いちばん下の立っているビットを取り出す
                Grid[best] = SudokuGrid.DigitOf(bit);
                Mark(best, bit);
                var done = Search();
                Unmark(best, bit);
                Grid[best] = 0;
                if (done) return true;
            }
            return false;
        }
    }
}
