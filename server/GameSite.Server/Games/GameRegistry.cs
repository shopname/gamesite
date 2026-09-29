using System.Reflection;

namespace GameSite.Server.Games;

/// <summary>
/// IGameLogic を実装したクラスをアセンブリから探して登録する。
/// 新しいゲームの C# クラスを追加するだけで使えるようになる（Program.cs の変更は不要）。
/// </summary>
public sealed class GameRegistry
{
    private readonly Dictionary<string, IGameLogic> _logics;

    public GameRegistry(IServiceProvider services, ILogger<GameRegistry> logger)
    {
        _logics = new Dictionary<string, IGameLogic>(StringComparer.Ordinal);
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IGameLogic).IsAssignableFrom(t));

        foreach (var type in types)
        {
            var logic = (IGameLogic)ActivatorUtilities.CreateInstance(services, type);
            if (!_logics.TryAdd(logic.GameId, logic))
            {
                logger.LogWarning("GameId \"{Id}\" のロジックが重複しています: {Type}", logic.GameId, type.FullName);
                continue;
            }
            logger.LogInformation("ゲームロジックを登録: {Id} ({Type})", logic.GameId, type.Name);
        }
    }

    public IGameLogic? Find(string gameId) => _logics.GetValueOrDefault(gameId);

    public bool Has(string gameId) => _logics.ContainsKey(gameId);
}
