namespace GameSite.Server.Core;

/// <summary>サイト全体の設定。appsettings.json の "Site" セクションから読み込む。</summary>
public sealed class SiteOptions
{
    public string Title { get; set; } = "TEMのゲームサイト";

    /// <summary>ホームページのフォルダ名（gamesite/ 直下）。</summary>
    public string HomeFolder { get; set; } = "00Home";

    public string HomePage { get; set; } = "homepage.html";

    /// <summary>公開から何日間 NEW バッジを付けるか。</summary>
    public int NewBadgeDays { get; set; } = 30;

    /// <summary>
    /// フロント側ファイル（00Home と各ゲームフォルダ）が置かれているディレクトリ。
    /// 空なら自動判定：公開後は出力先の site/、開発時は gamesite/（このプロジェクトの 2 階層上）。
    /// </summary>
    public string Root { get; set; } = "";

    public string HomeUrl => $"/{HomeFolder}/{HomePage}";

    public static string ResolveRoot(SiteOptions options, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(options.Root))
            return Path.GetFullPath(options.Root, env.ContentRootPath);

        var published = Path.Combine(env.ContentRootPath, "site");
        if (Directory.Exists(published))
            return published;

        return Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", ".."));
    }
}
