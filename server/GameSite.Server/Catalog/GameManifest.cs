namespace GameSite.Server.Catalog;

/// <summary>
/// 各ゲームフォルダに置く game.json の内容。
/// 必須は id と title のみ。それ以外は省略可能。
/// </summary>
public sealed class GameManifest
{
    /// <summary>一意な ID。お気に入り・履歴・ランキングのキーになるので公開後は変更しない。</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>ひらがなの読み（検索・名前順に使用）。</summary>
    public string? Reading { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = [];
    public string? Genre { get; set; }
    public string? Players { get; set; }
    public string? PlayTime { get; set; }
    public string[] Controls { get; set; } = [];
    public string[] HowToPlay { get; set; } = [];
    /// <summary>サムネイルが無い時に表示する絵文字や文字。</summary>
    public string? Icon { get; set; }
    public string? Color { get; set; }
    /// <summary>ゲームフォルダからの相対パス（例: "thumb.png"）。</summary>
    public string? Thumbnail { get; set; }
    /// <summary>起動ファイル（既定: index.html）。</summary>
    public string Entry { get; set; } = "index.html";
    /// <summary>公開日 "YYYY-MM-DD"。</summary>
    public string? Added { get; set; }
    public string? Updated { get; set; }
    /// <summary>"published"（既定）| "beta" | "coming-soon" | "hidden"</summary>
    public string Status { get; set; } = "published";
    public bool Featured { get; set; }
    /// <summary>難易度などのモード。ランキングはモードごとに集計される。</summary>
    public GameMode[] Modes { get; set; } = [];
}

public sealed class GameMode
{
    public string Id { get; set; } = "default";
    public string Label { get; set; } = "";
    /// <summary>詳細画面で最初に見せるランキングのモード。</summary>
    public bool Default { get; set; }
    /// <summary>
    /// 日付ごとにランキングを分けるモード（デイリー問題など）。
    /// true のとき、"{Id}-YYYY-MM-DD"（例: "daily-2026-09-30"）という日付付きのモードでランキングを取得できる。
    /// </summary>
    public bool Dated { get; set; }
}
