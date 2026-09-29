using System.Globalization;
using System.Text;

namespace GameSite.Server.Api;

/// <summary>
/// ログイン不要のプレイヤー識別。
/// ブラウザが初回に生成したランダムな GUID を X-Player-Id ヘッダーで送ってくる。
/// </summary>
public static class PlayerIdentity
{
    public const string HeaderName = "X-Player-Id";

    public static Guid? From(HttpContext ctx) =>
        Guid.TryParse(ctx.Request.Headers[HeaderName].ToString(), out var id) && id != Guid.Empty ? id : null;

    /// <summary>ランキングに表示する名前を整える（制御文字除去・最大 12 文字）。</summary>
    public static string SanitizeName(string? name)
    {
        var cleaned = new StringBuilder();
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormKC))
            if (!char.IsControl(c)) cleaned.Append(c);

        var text = cleaned.ToString().Trim();
        var info = new StringInfo(text);
        if (info.LengthInTextElements > 12) text = info.SubstringByTextElements(0, 12);
        return string.IsNullOrWhiteSpace(text) ? "ななしさん" : text;
    }
}
