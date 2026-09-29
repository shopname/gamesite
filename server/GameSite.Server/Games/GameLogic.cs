using System.Text.Json;

namespace GameSite.Server.Games;

/// <summary>
/// ゲームのロジック（C#）が実装するインターフェース。
/// このインターフェースを実装したクラスはサーバー起動時に自動で登録される。
/// GameId は、そのゲームのフォルダにある game.json の id と一致させる。
/// </summary>
public interface IGameLogic
{
    string GameId { get; }

    /// <summary>
    /// 新しいゲームを 1 回分開始する。
    /// options はブラウザから送られた任意の JSON（難易度など）。検証してから使うこと。
    /// </summary>
    GameSession CreateSession(JsonElement options, GameContext context);
}

/// <summary>セッション作成時に渡される共通の道具。Player はゲームを始めたプレイヤーの ID（X-Player-Id）。</summary>
public sealed record GameContext(Random Random, TimeProvider Time, Guid Player);

/// <summary>
/// 1 回分のゲーム状態。サーバーのメモリ上にだけ存在し、ブラウザには GetView() の結果だけが送られる。
/// 答えや山札などの「見せてはいけない情報」はここに持たせておけば、ブラウザ側からは見えない。
/// </summary>
public abstract class GameSession
{
    /// <summary>ランキングを分けるモード（難易度など）。game.json の modes[].id と対応。</summary>
    public string Mode { get; protected init; } = "default";

    public bool IsFinished { get; protected set; }

    /// <summary>ゲーム終了時の得点。null ならランキング対象外。</summary>
    public int? FinalScore { get; protected set; }

    /// <summary>ブラウザに見せてよい状態だけを返す。</summary>
    public abstract object GetView();

    /// <summary>ブラウザからの操作を検証して適用する。</summary>
    public abstract ActionOutcome Apply(JsonElement action);
}

/// <summary>
/// 操作を型付きで受け取れる GameSession。
/// ブラウザからの JSON が TAction に変換されて Handle に渡される。
/// </summary>
public abstract class GameSession<TAction> : GameSession where TAction : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed override ActionOutcome Apply(JsonElement action)
    {
        if (IsFinished) return ActionOutcome.Invalid("このゲームはすでに終了しています。");

        TAction? typed;
        try { typed = action.Deserialize<TAction>(Json); }
        catch (JsonException) { typed = null; }

        return typed is null ? ActionOutcome.Invalid("操作の形式が正しくありません。") : Handle(typed);
    }

    protected abstract ActionOutcome Handle(TAction action);
}

/// <summary>
/// 操作の結果。Events はアニメーションなど「何が起きたか」をブラウザに伝えるために使う。
/// </summary>
public sealed record ActionOutcome(bool Ok, string? Error, IReadOnlyList<object> Events)
{
    public static ActionOutcome Success(params object[] events) => new(true, null, events);
    public static ActionOutcome Invalid(string message) => new(false, message, []);
}
