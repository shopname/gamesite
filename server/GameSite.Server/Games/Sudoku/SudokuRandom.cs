namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 種（シード）が同じなら、どの環境でも必ず同じ乱数列を返す乱数（SplitMix64）。
///
/// System.Random はシード付きでも、.NET のバージョンによって出てくる数が変わる可能性がある。
/// デイリー問題は「その日の全員に同じ盤面」を配る必要があるので、アルゴリズムを自前で固定している。
/// </summary>
public sealed class SudokuRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>0 以上 max 未満の整数。</summary>
    public int Next(int max) => (int)(NextULong() % (ulong)max);

    public void Shuffle<T>(T[] items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>文字列から 64 ビットの種を作る（FNV-1a ハッシュ）。日付からデイリーの種を作るのに使う。</summary>
    public static ulong SeedFrom(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// 数独の「見た目を変えても中身は同じ」変換。
///   - 数字の入れ替え（1→7、2→3 …）
///   - 同じ段（3 行の束）の中での行の入れ替え、段どうしの入れ替え（列も同様）
///   - 転置（行と列を入れ替える）
/// これらの変換では、初期数字の個数・解の一意性・必要な技法（＝難易度）が変わらない。
/// 1 問の元データから約 1.2 兆通りの「別の見た目の問題」が作れるので、
/// 手元の問題集から出しても、同じ盤面に当たることはまずない。
/// </summary>
public static class SudokuTransform
{
    public static int[] Apply(int[] grid, SudokuRandom rng)
    {
        var digitMap = Enumerable.Range(0, 10).ToArray();   // digitMap[元の数字] = 新しい数字（0 は空きのまま）
        var shuffled = Enumerable.Range(1, 9).ToArray();
        rng.Shuffle(shuffled);
        for (var d = 1; d <= 9; d++) digitMap[d] = shuffled[d - 1];

        var rows = LinePermutation(rng);                     // rows[新しい行] = 元の行
        var cols = LinePermutation(rng);
        var transpose = rng.Next(2) == 1;

        var result = new int[SudokuGrid.Cells];
        for (var r = 0; r < 9; r++)
        {
            for (var c = 0; c < 9; c++)
            {
                var src = transpose ? cols[c] * 9 + rows[r] : rows[r] * 9 + cols[c];
                result[r * 9 + c] = digitMap[grid[src]];
            }
        }
        return result;
    }

    /// <summary>段（3 本の束）ごと並べ替え、段の中でも並べ替える。ルールを壊さない並べ替えはこの形だけ。</summary>
    private static int[] LinePermutation(SudokuRandom rng)
    {
        var bands = new[] { 0, 1, 2 };
        rng.Shuffle(bands);
        var result = new int[9];
        for (var b = 0; b < 3; b++)
        {
            var inner = new[] { 0, 1, 2 };
            rng.Shuffle(inner);
            for (var i = 0; i < 3; i++) result[b * 3 + i] = bands[b] * 3 + inner[i];
        }
        return result;
    }
}
