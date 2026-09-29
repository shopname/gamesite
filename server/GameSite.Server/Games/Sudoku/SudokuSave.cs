using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace GameSite.Server.Games.Sudoku;

/// <summary>
/// 途中経過のセーブデータ。ブラウザの localStorage に「署名付き」で保存してもらう。
///
/// ■ なぜ必要か
///   プレイ中のゲームはサーバーのメモリにあり、30 分操作がないか、サーバーが再起動・スリープすると消える。
///   仕様「画面を閉じても続きから再開できる」を満たすため、サーバーが消えても復元できる材料を
///   ブラウザ側に持たせておく。
///
/// ■ なぜ署名するか
///   ブラウザの保存内容は開発者ツールで書き換えられる。ミス回数や経過時間を減らされると
///   ランキングが壊れるので、サーバーだけが知る鍵で HMAC-SHA256 の署名を付け、
///   1 文字でも書き換えられたら復元を断る。
/// </summary>
/// <param name="Nonce">1 回のゲームを表すランダムな ID。</param>
/// <param name="Seq">状態が変わるたびに増える番号。古いセーブの使い回しを見つけるのに使う。</param>
/// <param name="Placed">プレイヤーが正しく確定した数字（81 文字、なしは '0'）。</param>
/// <param name="Paused">一時停止中に作られたセーブか。</param>
public sealed record SudokuSave(
    int V,
    string Nonce,
    int Seq,
    string Difficulty,
    string? Daily,
    string Givens,
    string Placed,
    int Mistakes,
    long ElapsedMs,
    bool Paused,
    bool Ranked,
    string? UnrankedReason);

/// <summary>セーブデータへの署名と検証。</summary>
public sealed class SudokuSaveSigner
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _key;

    public SudokuSaveSigner(byte[] key) => _key = key;

    /// <summary>
    /// 署名の鍵を用意する。優先順:
    ///   1. 設定 Sudoku:SigningKey（公開サーバー用。Render では render.yaml で自動生成）
    ///   2. データフォルダの sudoku-signing.key（手元の PC 用。初回に自動作成）
    ///   3. 起動ごとのランダムな鍵（1・2 が使えないとき。再起動するとそれ以前のセーブは復元できない）
    /// </summary>
    public static SudokuSaveSigner Create(IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var configured = config["Sudoku:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
            return new SudokuSaveSigner(SHA256.HashData(Encoding.UTF8.GetBytes(configured)));

        try
        {
            var dbPath = Path.GetFullPath(config["Data:Path"] ?? "App_Data/gamesite.db", env.ContentRootPath);
            var keyPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sudoku-signing.key");
            if (!File.Exists(keyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            }
            return new SudokuSaveSigner(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogWarning(ex, "数独のセーブ用の鍵を保存できませんでした。再起動すると中断中のゲームは復元できません。");
            return new SudokuSaveSigner(RandomNumberGenerator.GetBytes(32));
        }
    }

    /// <summary>"本文.署名" の形（どちらも URL で使える Base64）にする。</summary>
    public string Sign(SudokuSave save)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(save, Json);
        return WebEncoders.Base64UrlEncode(body) + "." + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, body));
    }

    /// <summary>署名が正しければ中身を返す。改ざん・形式違い・古い版は null。</summary>
    public SudokuSave? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        try
        {
            var body = WebEncoders.Base64UrlDecode(token[..dot]);
            var mac = WebEncoders.Base64UrlDecode(token[(dot + 1)..]);
            // 比較にかかる時間から正しい署名を推測されないよう、一定時間で比べる
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_key, body))) return null;
            var save = JsonSerializer.Deserialize<SudokuSave>(body, Json);
            return save?.V == Version ? save : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// ランキングの不正を防ぐための「記憶」。サーバーのメモリに数日だけ保持する。
///   - 各ゲーム（Nonce）の最新のセーブ番号 … 古いセーブで「ミスの前」に巻き戻す使い回しを見つける
///   - デイリー問題に挑戦したプレイヤー     … ランキング対象は各自その日の最初の 1 回だけにする
/// メモリなので、サーバーの再起動で忘れる（その場合も署名の検証は効く）。
/// </summary>
public sealed class SudokuGuard : IDisposable
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(2);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 200_000 });

    public void RecordSeq(string nonce, int seq) =>
        _cache.Set("seq:" + nonce, seq, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Keep, Size = 1 });

    /// <summary>このセーブより新しい状態を、サーバーがすでに見ているか。</summary>
    public bool IsStale(string nonce, int seq) => _cache.TryGetValue("seq:" + nonce, out int latest) && latest > seq;

    /// <summary>
    /// このゲーム（Nonce）を操作できるセッションを「今作ったもの」に切り替え、その世代番号を返す。
    /// 同じセーブを 2 つの画面で開き、片方で数字を試してもう片方で続ける、といった抜け道を防ぐ。
    /// </summary>
    public int Claim(string nonce)
    {
        lock (_cache)
        {
            var generation = _cache.TryGetValue("gen:" + nonce, out int g) ? g + 1 : 1;
            _cache.Set("gen:" + nonce, generation, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Keep, Size = 1 });
            return generation;
        }
    }

    /// <summary>このセッションが、今もそのゲームを操作できる最新のセッションか。</summary>
    public bool IsCurrent(string nonce, int generation) =>
        !_cache.TryGetValue("gen:" + nonce, out int g) || g == generation;

    /// <summary>今日のデイリーに初めて挑戦するなら true を返し、挑戦済みとして記録する。</summary>
    public bool TryStartDaily(Guid player, DateOnly date)
    {
        var key = $"daily:{player:N}:{date:yyyyMMdd}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out _)) return false;
            _cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Keep, Size = 1 });
            return true;
        }
    }

    public void Dispose() => _cache.Dispose();
}
