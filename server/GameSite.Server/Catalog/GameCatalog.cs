using System.Text.Json;
using GameSite.Server.Core;
using Microsoft.Extensions.Options;

namespace GameSite.Server.Catalog;

public enum GameStatus { Published, Beta, ComingSoon, Hidden }

/// <summary>game.json を正規化した、サーバー内部で扱うゲーム情報。</summary>
public sealed class Game
{
    public required GameManifest Manifest { get; init; }
    public required string Folder { get; init; }
    public required string Url { get; init; }
    public string? ThumbnailUrl { get; init; }
    public required string Icon { get; init; }
    public required string Color { get; init; }
    public DateOnly? Added { get; init; }
    public DateOnly? Updated { get; init; }
    public GameStatus Status { get; init; }
    public required string SearchText { get; init; }
    public required string SortKey { get; init; }

    public string Id => Manifest.Id;
    public string Title => Manifest.Title;
    public bool Playable => Status is GameStatus.Published or GameStatus.Beta;
    public bool Listed => Status != GameStatus.Hidden;
    public bool IsNew(DateOnly today, int days) => Playable && Added is { } a && today.DayNumber - a.DayNumber < days;
    public DateOnly? LastChanged => Updated > Added ? Updated : Added;

    /// <summary>ランキングのモード ID を検証する。未定義なら default 指定のモード → 先頭 → "default"。</summary>
    public string ResolveMode(string? mode)
    {
        var modes = Manifest.Modes;
        if (modes.Length == 0) return "default";
        return (modes.FirstOrDefault(m => m.Id == mode) ?? modes.FirstOrDefault(m => m.Default) ?? modes[0]).Id;
    }
}

/// <summary>
/// gamesite/ 直下の各フォルダにある game.json を走査してゲーム一覧を作る。
/// フォルダを追加するだけで自動的に一覧に載る（サーバーの再起動は不要）。
/// </summary>
public sealed class GameCatalog
{
    public const string ManifestFileName = "game.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly string[] Palette = ["#d9481f", "#3d7ea6", "#7a4fd0", "#2f9e6b", "#c7851a", "#c23b6d", "#3b5bd9"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SiteOptions _options;
    private readonly ILogger<GameCatalog> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Snapshot? _snapshot;

    public GameCatalog(IOptions<SiteOptions> options, IHostEnvironment env, ILogger<GameCatalog> logger, TimeProvider time)
    {
        _options = options.Value;
        _logger = logger;
        _time = time;
        Root = SiteOptions.ResolveRoot(_options, env);
        _logger.LogInformation("サイトのルート: {Root}", Root);
    }

    public string Root { get; }

    public IReadOnlyList<Game> All => Current.Games;

    public Game? Find(string id) => Current.ById.GetValueOrDefault(id);

    /// <summary>静的ファイルとして配信してよいフォルダか（ホームと登録済みゲームのみ）。</summary>
    public bool IsServedFolder(string folder) =>
        string.Equals(folder, _options.HomeFolder, StringComparison.OrdinalIgnoreCase) || Current.Folders.Contains(folder);

    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private Snapshot Current
    {
        get
        {
            var now = _time.GetUtcNow();
            var snap = _snapshot;
            if (snap is not null && now - snap.LoadedAt < RefreshInterval) return snap;
            lock (_gate)
            {
                if (_snapshot is null || now - _snapshot.LoadedAt >= RefreshInterval)
                    _snapshot = Load(now);
                return _snapshot;
            }
        }
    }

    private Snapshot Load(DateTimeOffset now)
    {
        var games = new List<Game>();
        var byId = new Dictionary<string, Game>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(Root))
        {
            _logger.LogWarning("サイトのルートが見つかりません: {Root}", Root);
            return new Snapshot(now, games, byId, folders);
        }

        foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(dir);
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (folder.StartsWith('.') || !File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Title))
                {
                    _logger.LogWarning("{Path}: id と title は必須です。", manifestPath);
                    continue;
                }
                if (byId.ContainsKey(manifest.Id))
                {
                    _logger.LogWarning("{Path}: id \"{Id}\" が重複しています。", manifestPath, manifest.Id);
                    continue;
                }

                var game = Normalize(manifest, folder, games.Count);
                folders.Add(folder);
                byId[game.Id] = game;
                games.Add(game);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "{Path} を読み込めませんでした。", manifestPath);
            }
        }

        return new Snapshot(now, games, byId, folders);
    }

    private static Game Normalize(GameManifest m, string folder, int index)
    {
        var baseUrl = "/" + Uri.EscapeDataString(folder) + "/";
        var entryPath = string.IsNullOrWhiteSpace(m.Entry) ? "index.html" : m.Entry;
        var entry = string.Join('/', entryPath.Split('/').Select(Uri.EscapeDataString));
        var thumb = string.IsNullOrWhiteSpace(m.Thumbnail) ? null
            : m.Thumbnail.StartsWith("http", StringComparison.OrdinalIgnoreCase) || m.Thumbnail.StartsWith('/') ? m.Thumbnail
            : baseUrl + m.Thumbnail;

        return new Game
        {
            Manifest = m,
            Folder = folder,
            Url = baseUrl + entry,
            ThumbnailUrl = thumb,
            Icon = string.IsNullOrWhiteSpace(m.Icon) ? m.Title[..1] : m.Icon,
            Color = string.IsNullOrWhiteSpace(m.Color) ? Palette[index % Palette.Length] : m.Color,
            Added = ParseDate(m.Added),
            Updated = ParseDate(m.Updated),
            Status = ParseStatus(m.Status),
            SortKey = TextNormalizer.Normalize(m.Reading ?? m.Title),
            SearchText = TextNormalizer.Normalize(string.Join(' ',
                new[] { m.Title, m.Reading, m.Summary, m.Description, m.Genre }.Concat(m.Tags))),
        };
    }

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d : null;

    private static GameStatus ParseStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "beta" => GameStatus.Beta,
        "coming-soon" => GameStatus.ComingSoon,
        "hidden" => GameStatus.Hidden,
        _ => GameStatus.Published,
    };

    private sealed record Snapshot(
        DateTimeOffset LoadedAt,
        IReadOnlyList<Game> Games,
        IReadOnlyDictionary<string, Game> ById,
        IReadOnlySet<string> Folders);
}
