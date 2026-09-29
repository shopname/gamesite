namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 解くために必要な「技法」の段階。数字が大きいほど難しい。
/// 難易度は「その問題を解き切るのに必要だった、いちばん難しい技法」で決める。
/// </summary>
public enum SudokuLevel
{
    /// <summary>ネイキッドシングル / ヒドゥンシングル（そのマス・その場所しかない）だけで解ける。</summary>
    Singles = 1,
    /// <summary>ロックされた候補（ポインティング / クレーミング）が必要。</summary>
    Intersections = 2,
    /// <summary>ネイキッド / ヒドゥン ペア・トリプル が必要。</summary>
    Subsets = 3,
    /// <summary>X-Wing / Swordfish / XY-Wing / クアッド など上級技法が必要。</summary>
    Advanced = 4,
    /// <summary>上の技法では行き詰まる。仮置き・試行錯誤（背理法）が必要。</summary>
    Trial = 5,
}

/// <param name="Level">必要だった最難の技法。</param>
/// <param name="Steps">技法を適用した回数の合計。</param>
/// <param name="HardSteps">Subsets 以上の技法を使った回数（同じ段階の中での難しさの目安）。</param>
public sealed record SudokuRating(SudokuLevel Level, int Steps, int HardSteps);

/// <summary>
/// 人間の解き方を真似て問題を解き、難しさを判定する（論理ソルバー）。
///
/// やり方:
///   1. 全マスの候補（ペンシルマーク）を求める
///   2. やさしい技法から順に試し、1 つでも進展（数字の確定 or 候補の削除）があれば 1 に戻る
///   3. すべて埋まれば、使った最難の技法がその問題の難易度
///   4. どの技法でも進展しなければ「仮置きが必要」（Trial）と判定する
///
/// 研究（Nature Sci. Rep. 2, 725）でも示されているとおり、初期数字の個数は難しさをほとんど表さない。
/// 17 個ヒントでもシングルだけで解ける問題は多く、逆に 25 個でも仮置きが要る問題がある。
/// だから出題はこの判定結果で選ぶ。
/// </summary>
public sealed class SudokuRater
{
    private readonly int[] _value = new int[SudokuGrid.Cells]; // 確定した数字（0 = 空き）
    private readonly int[] _cand = new int[SudokuGrid.Cells];  // 空きマスの候補（ビットマスク）
    private bool _broken;                                      // 矛盾が見つかった（正しい問題なら起きない）

    private SudokuRater(int[] puzzle)
    {
        Array.Fill(_cand, SudokuGrid.AllDigits);
        for (var cell = 0; cell < SudokuGrid.Cells; cell++)
            if (puzzle[cell] != 0) Place(cell, puzzle[cell]);
    }

    /// <summary>問題の難しさを判定する。puzzle は一意解であることを前提とする。</summary>
    public static SudokuRating Rate(int[] puzzle)
    {
        var r = new SudokuRater(puzzle);
        var level = SudokuLevel.Singles;
        int steps = 0, hard = 0;

        while (!r.IsSolved)
        {
            if (r._broken) return new SudokuRating(SudokuLevel.Trial, steps, hard);

            SudokuLevel used;
            if (r.NakedSingle() || r.HiddenSingle()) used = SudokuLevel.Singles;
            else if (r.LockedCandidates()) used = SudokuLevel.Intersections;
            else if (r.NakedSubset(2) || r.HiddenSubset(2) || r.NakedSubset(3) || r.HiddenSubset(3)) used = SudokuLevel.Subsets;
            else if (r.Fish(2) || r.XyWing() || r.Fish(3) || r.NakedSubset(4) || r.HiddenSubset(4)) used = SudokuLevel.Advanced;
            else return new SudokuRating(SudokuLevel.Trial, steps, hard); // 論理だけでは行き詰まった

            steps++;
            if (used >= SudokuLevel.Subsets) hard++;
            if (used > level) level = used;
        }
        return new SudokuRating(level, steps, hard);
    }

    private bool IsSolved => Array.TrueForAll(_value, v => v != 0);

    /// <summary>数字を確定し、ピア（同じ行・列・ブロック）の候補からその数字を消す。</summary>
    private void Place(int cell, int digit)
    {
        var bit = SudokuGrid.Bit(digit);
        if ((_cand[cell] & bit) == 0) _broken = true;
        _value[cell] = digit;
        _cand[cell] = 0;
        foreach (var p in SudokuGrid.Peers[cell])
        {
            if (_value[p] == digit) _broken = true;
            _cand[p] &= ~bit;
        }
    }

    /// <summary>候補を消す。実際に消えたら true（＝進展あり）。</summary>
    private bool Eliminate(int cell, int mask)
    {
        if (_value[cell] != 0 || (_cand[cell] & mask) == 0) return false;
        _cand[cell] &= ~mask;
        if (_cand[cell] == 0) _broken = true;
        return true;
    }

    // ------------------------------------------------------------------
    // 段階 1: シングル
    // ------------------------------------------------------------------

    /// <summary>ネイキッドシングル: 候補が 1 つしかないマスは、その数字で確定。</summary>
    private bool NakedSingle()
    {
        for (var cell = 0; cell < SudokuGrid.Cells; cell++)
        {
            if (_value[cell] != 0) continue;
            if (_cand[cell] == 0) { _broken = true; return false; }
            if (SudokuGrid.Count(_cand[cell]) == 1)
            {
                Place(cell, SudokuGrid.DigitOf(_cand[cell]));
                return true;
            }
        }
        return false;
    }

    /// <summary>ヒドゥンシングル: あるユニットで、ある数字が入れる場所が 1 マスだけなら確定。</summary>
    private bool HiddenSingle()
    {
        foreach (var unit in SudokuGrid.Units)
        {
            for (var d = 1; d <= 9; d++)
            {
                var bit = SudokuGrid.Bit(d);
                int where = -1, count = 0;
                var already = false;
                foreach (var cell in unit)
                {
                    if (_value[cell] == d) { already = true; break; }
                    if ((_cand[cell] & bit) != 0) { where = cell; count++; }
                }
                if (already) continue;
                if (count == 0) { _broken = true; return false; }
                if (count == 1) { Place(where, d); return true; }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 段階 2: ロックされた候補
    // ------------------------------------------------------------------

    /// <summary>
    /// ポインティング: ブロック内で数字 d の候補が 1 つの行（列）に収まっていれば、
    ///   その行（列）のブロック外の d は消せる。
    /// クレーミング: 行（列）内で数字 d の候補が 1 つのブロックに収まっていれば、
    ///   そのブロックの行（列）外の d は消せる。
    /// </summary>
    private bool LockedCandidates()
    {
        for (var d = 1; d <= 9; d++)
        {
            var bit = SudokuGrid.Bit(d);
            for (var u = 0; u < 27; u++)
            {
                var cells = SudokuGrid.Units[u].Where(c => (_cand[c] & bit) != 0).ToArray();
                if (cells.Length < 2) continue;

                if (u >= 18)
                {
                    // ブロック → 行・列（ポインティング）
                    if (cells.All(c => SudokuGrid.Row(c) == SudokuGrid.Row(cells[0])) &&
                        EliminateOutside(SudokuGrid.Units[SudokuGrid.Row(cells[0])], cells, bit)) return true;
                    if (cells.All(c => SudokuGrid.Col(c) == SudokuGrid.Col(cells[0])) &&
                        EliminateOutside(SudokuGrid.Units[9 + SudokuGrid.Col(cells[0])], cells, bit)) return true;
                }
                else if (cells.All(c => SudokuGrid.Box(c) == SudokuGrid.Box(cells[0])) &&
                         EliminateOutside(SudokuGrid.Units[18 + SudokuGrid.Box(cells[0])], cells, bit))
                {
                    // 行・列 → ブロック（クレーミング）
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>unit のうち keep 以外のマスから mask を消す。</summary>
    private bool EliminateOutside(int[] unit, int[] keep, int mask)
    {
        var changed = false;
        foreach (var cell in unit)
            if (Array.IndexOf(keep, cell) < 0) changed |= Eliminate(cell, mask);
        return changed;
    }

    // ------------------------------------------------------------------
    // 段階 3〜4: サブセット（ペア・トリプル・クアッド）
    // ------------------------------------------------------------------

    /// <summary>
    /// ネイキッドサブセット: ユニット内の k マスの候補を合わせると、ちょうど k 種類の数字しかない。
    ///   → その k 種類はこの k マスで使い切るので、ユニットの他のマスから消せる。
    /// </summary>
    private bool NakedSubset(int k)
    {
        foreach (var unit in SudokuGrid.Units)
        {
            var open = unit.Where(c => _value[c] == 0 && SudokuGrid.Count(_cand[c]) <= k).ToArray();
            if (open.Length < k) continue;

            foreach (var pick in Combinations(open.Length, k))
            {
                var union = 0;
                foreach (var i in pick) union |= _cand[open[i]];
                if (SudokuGrid.Count(union) != k) continue;

                var chosen = pick.Select(i => open[i]).ToArray();
                if (EliminateOutside(unit, chosen, union)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// ヒドゥンサブセット: ユニット内で k 種類の数字の入れる場所を合わせると、ちょうど k マスしかない。
    ///   → その k マスにはこの k 種類しか入らないので、それ以外の候補を消せる。
    /// </summary>
    private bool HiddenSubset(int k)
    {
        foreach (var unit in SudokuGrid.Units)
        {
            // 数字ごとに「入れる場所」を、ユニット内の位置（0〜8）のビット集合で表す
            var places = new int[10];
            var digits = new List<int>();
            for (var d = 1; d <= 9; d++)
            {
                var bit = SudokuGrid.Bit(d);
                for (var i = 0; i < 9; i++)
                    if ((_cand[unit[i]] & bit) != 0) places[d] |= 1 << i;
                var n = SudokuGrid.Count(places[d]);
                if (n >= 1 && n <= k) digits.Add(d);
            }
            if (digits.Count < k) continue;

            foreach (var pick in Combinations(digits.Count, k))
            {
                int where = 0, keepMask = 0;
                foreach (var i in pick)
                {
                    where |= places[digits[i]];
                    keepMask |= SudokuGrid.Bit(digits[i]);
                }
                if (SudokuGrid.Count(where) != k) continue;

                var changed = false;
                for (var i = 0; i < 9; i++)
                    if ((where & (1 << i)) != 0) changed |= Eliminate(unit[i], SudokuGrid.AllDigits & ~keepMask);
                if (changed) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 段階 4: 上級技法
    // ------------------------------------------------------------------

    /// <summary>
    /// フィッシュ（k = 2: X-Wing、k = 3: Swordfish）。
    /// 数字 d について、k 本の行で候補の列を合わせるとちょうど k 列に収まるなら、
    /// その k 列の「他の行」から d を消せる（行と列を入れ替えた形も調べる）。
    /// </summary>
    private bool Fish(int k)
    {
        for (var d = 1; d <= 9; d++)
        {
            var bit = SudokuGrid.Bit(d);
            foreach (var byRow in new[] { true, false })
            {
                // lines[i] = i 本目の線（行 or 列）で、d が入れる位置のビット集合
                var lines = new int[9];
                var usable = new List<int>();
                for (var line = 0; line < 9; line++)
                {
                    for (var pos = 0; pos < 9; pos++)
                    {
                        var cell = byRow ? line * 9 + pos : pos * 9 + line;
                        if ((_cand[cell] & bit) != 0) lines[line] |= 1 << pos;
                    }
                    var n = SudokuGrid.Count(lines[line]);
                    if (n >= 2 && n <= k) usable.Add(line);
                }
                if (usable.Count < k) continue;

                foreach (var pick in Combinations(usable.Count, k))
                {
                    var cover = 0;
                    var baseLines = 0;
                    foreach (var i in pick)
                    {
                        cover |= lines[usable[i]];
                        baseLines |= 1 << usable[i];
                    }
                    if (SudokuGrid.Count(cover) != k) continue;

                    var changed = false;
                    for (var pos = 0; pos < 9; pos++)
                    {
                        if ((cover & (1 << pos)) == 0) continue;
                        for (var line = 0; line < 9; line++)
                        {
                            if ((baseLines & (1 << line)) != 0) continue;
                            var cell = byRow ? line * 9 + pos : pos * 9 + line;
                            changed |= Eliminate(cell, bit);
                        }
                    }
                    if (changed) return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// XY-Wing: 候補 {a,b} のマス（軸）と、軸から見える {a,c}・{b,c} の 2 マス（はさみ）。
    /// 軸が a でも b でも、どちらかのはさみが c になる。
    /// → 両方のはさみから見えるマスには c が入らない。
    /// </summary>
    private bool XyWing()
    {
        for (var pivot = 0; pivot < SudokuGrid.Cells; pivot++)
        {
            var pc = _cand[pivot];
            if (_value[pivot] != 0 || SudokuGrid.Count(pc) != 2) continue;

            var wings = SudokuGrid.Peers[pivot]
                .Where(p => _value[p] == 0 && SudokuGrid.Count(_cand[p]) == 2 && SudokuGrid.Count(_cand[p] & pc) == 1)
                .ToArray();

            for (var i = 0; i < wings.Length; i++)
            {
                for (var j = i + 1; j < wings.Length; j++)
                {
                    int w1 = _cand[wings[i]], w2 = _cand[wings[j]];
                    var c = w1 & w2 & ~pc;                         // はさみ同士の共通の数字（軸にはない）
                    if (SudokuGrid.Count(c) != 1) continue;
                    if (((w1 | w2) & pc) != pc || (w1 & pc) == (w2 & pc)) continue; // a と b を 1 つずつ持つ

                    var changed = false;
                    foreach (var target in SudokuGrid.Peers[wings[i]])
                    {
                        if (target == wings[j] || target == pivot) continue;
                        if (Array.IndexOf(SudokuGrid.Peers[wings[j]], target) < 0) continue;
                        changed |= Eliminate(target, c);
                    }
                    if (changed) return true;
                }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------

    /// <summary>0〜n-1 から k 個選ぶ組み合わせをすべて列挙する（n は最大 9 なので全探索で十分）。</summary>
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        for (var mask = 0; mask < 1 << n; mask++)
        {
            if (SudokuGrid.Count(mask) != k) continue;
            var pick = new int[k];
            var idx = 0;
            for (var i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0) pick[idx++] = i;
            yield return pick;
        }
    }
}
