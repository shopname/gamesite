// ゲームのひな形（C# 側のロジック）。
// このファイルを server/GameSite.Server/Games/<ゲーム名>/ にコピーして使う。
// IGameLogic を実装したクラスはサーバー起動時に自動登録されるので、Program.cs の変更は不要。
using System.Text.Json;

namespace GameSite.Server.Games.Guess;

public sealed class GuessGameLogic : IGameLogic
{
    // game.json の id と同じにする
    public string GameId => "guess";

    public GameSession CreateSession(JsonElement options, GameContext context) =>
        new GuessSession(context.Random);
}

/// <summary>ブラウザから送られてくる操作。{ "number": 42 }</summary>
public sealed record GuessAction(int Number);

public sealed class GuessSession : GameSession<GuessAction>
{
    private readonly int _answer; // 答えはサーバーのメモリにだけある（ブラウザからは見えない）
    private int _tries;

    public GuessSession(Random random) => _answer = random.Next(1, 101);

    protected override ActionOutcome Handle(GuessAction action)
    {
        if (action.Number is < 1 or > 100)
            return ActionOutcome.Invalid("1〜100 の数を入れてください。");

        _tries++;
        if (action.Number == _answer)
        {
            IsFinished = true;
            FinalScore = Math.Max(100, 1100 - _tries * 100); // 得点はサーバーで計算する（改ざんできない）
            return ActionOutcome.Success(new { type = "correct", tries = _tries });
        }
        return ActionOutcome.Success(new { type = action.Number < _answer ? "higher" : "lower" });
    }

    /// <summary>ブラウザに見せてよい情報だけを返す（答えは含めない）。</summary>
    public override object GetView() => new { tries = _tries, finished = IsFinished, score = FinalScore };
}
