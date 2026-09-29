using System.Text;

namespace GameSite.Server.Core;

/// <summary>
/// 検索用の文字列正規化。
/// 全角/半角（NFKC）、大文字/小文字、カタカナ/ひらがなの違いを吸収する。
/// 例: "ﾊﾟｽﾞﾙ" / "パズル" / "ぱずる" はすべて "ぱずる" になる。
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            // カタカナ（ァ〜ヶ）→ ひらがな
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        }
        return sb.ToString();
    }

    public static string[] Tokenize(string? query) =>
        Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
