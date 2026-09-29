using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace GameSite.Server.Data;

public sealed record PlayStat(int Count, DateTimeOffset LastPlayedAt);

/// <summary>1 人のプレイヤーのお気に入りとプレイ履歴。</summary>
public sealed record PlayerSnapshot(IReadOnlySet<string> Favorites, IReadOnlyDictionary<string, PlayStat> Plays)
{
    public static readonly PlayerSnapshot Empty = new(new HashSet<string>(), new Dictionary<string, PlayStat>());
}

public sealed record LeaderboardEntry(int Rank, string Name, int Score, DateTimeOffset At, bool IsMe);

/// <summary>
/// プレイヤーごとのデータ（お気に入り・履歴）とランキングを保存する。
/// - 接続文字列 ConnectionStrings:Postgres があれば PostgreSQL（クラウド公開用。Neon など）
/// - 無ければ SQLite ファイル（自分の PC で動かす用）
/// SQL は両方で動く書き方に揃えてある。
/// プレイヤーはログイン不要で、ブラウザが生成したランダムな ID（GUID）で識別する。
/// </summary>
public sealed class PlayerStore : IAsyncDisposable
{
    private readonly Func<DbConnection> _createConnection;
    private readonly NpgsqlDataSource? _postgres;
    private readonly TimeProvider _time;

    public PlayerStore(IConfiguration config, IHostEnvironment env, TimeProvider time, ILogger<PlayerStore> logger)
    {
        _time = time;

        var pg = config.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(pg))
        {
            _postgres = NpgsqlDataSource.Create(PostgresConnectionString.Normalize(pg));
            _createConnection = () => _postgres.CreateConnection();
            logger.LogInformation("データの保存先: PostgreSQL ({Host})", new NpgsqlConnectionStringBuilder(_postgres.ConnectionString).Host);
            return;
        }

        var path = Path.GetFullPath(config["Data:Path"] ?? "App_Data/gamesite.db", env.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sqlite = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _createConnection = () => new SqliteConnection(sqlite);
        logger.LogInformation("データの保存先: SQLite ({Path})", path);
    }

    private bool IsPostgres => _postgres is not null;

    public async Task InitializeAsync()
    {
        await using var db = await OpenAsync();
        if (!IsPostgres) await ExecAsync(db, "PRAGMA journal_mode = WAL;");

        var scoreId = IsPostgres ? "BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY" : "INTEGER PRIMARY KEY AUTOINCREMENT";
        await ExecAsync(db, $"""
            CREATE TABLE IF NOT EXISTS favorites (
                player_id  TEXT   NOT NULL,
                game_id    TEXT   NOT NULL,
                created_at BIGINT NOT NULL,
                PRIMARY KEY (player_id, game_id)
            );
            CREATE TABLE IF NOT EXISTS plays (
                player_id  TEXT    NOT NULL,
                game_id    TEXT    NOT NULL,
                play_count INTEGER NOT NULL,
                last_at    BIGINT  NOT NULL,
                PRIMARY KEY (player_id, game_id)
            );
            CREATE INDEX IF NOT EXISTS ix_plays_game ON plays (game_id);
            CREATE TABLE IF NOT EXISTS scores (
                id         {scoreId},
                game_id    TEXT    NOT NULL,
                mode       TEXT    NOT NULL,
                player_id  TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                score      INTEGER NOT NULL,
                created_at BIGINT  NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_scores_board ON scores (game_id, mode, score DESC);
            """);
    }

    // ------------------------------------------------------------------
    // お気に入り・履歴
    // ------------------------------------------------------------------
    public async Task<PlayerSnapshot> GetSnapshotAsync(Guid? player)
    {
        if (player is null) return PlayerSnapshot.Empty;
        await using var db = await OpenAsync();

        var favorites = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = Command(db, "SELECT game_id FROM favorites WHERE player_id = @p", ("@p", Key(player))))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) favorites.Add(r.GetString(0));

        var plays = new Dictionary<string, PlayStat>(StringComparer.Ordinal);
        await using (var cmd = Command(db, "SELECT game_id, play_count, last_at FROM plays WHERE player_id = @p", ("@p", Key(player))))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                plays[r.GetString(0)] = new PlayStat(Convert.ToInt32(r.GetValue(1)), FromMs(r.GetValue(2)));

        return new PlayerSnapshot(favorites, plays);
    }

    /// <summary>ゲームごとの全プレイヤー合計プレイ回数（人気順に使う）。</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetTotalPlaysAsync()
    {
        await using var db = await OpenAsync();
        await using var cmd = Command(db, "SELECT game_id, SUM(play_count) FROM plays GROUP BY game_id");
        await using var r = await cmd.ExecuteReaderAsync();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await r.ReadAsync()) result[r.GetString(0)] = Convert.ToInt64(r.GetValue(1));
        return result;
    }

    public async Task SetFavoriteAsync(Guid player, string gameId, bool on)
    {
        await using var db = await OpenAsync();
        var sql = on
            ? """
              INSERT INTO favorites (player_id, game_id, created_at) VALUES (@p, @g, @t)
              ON CONFLICT (player_id, game_id) DO NOTHING
              """
            : "DELETE FROM favorites WHERE player_id = @p AND game_id = @g";
        await using var cmd = Command(db, sql, ("@p", Key(player)), ("@g", gameId), ("@t", Now()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordPlayAsync(Guid player, string gameId)
    {
        await using var db = await OpenAsync();
        await using var cmd = Command(db, """
            INSERT INTO plays (player_id, game_id, play_count, last_at) VALUES (@p, @g, 1, @t)
            ON CONFLICT (player_id, game_id) DO UPDATE SET play_count = plays.play_count + 1, last_at = excluded.last_at
            """, ("@p", Key(player)), ("@g", gameId), ("@t", Now()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearHistoryAsync(Guid player)
    {
        await using var db = await OpenAsync();
        await using var cmd = Command(db, "DELETE FROM plays WHERE player_id = @p", ("@p", Key(player)));
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------
    // ランキング（プレイヤーごとの自己ベストで集計）
    // ------------------------------------------------------------------
    public async Task<int> AddScoreAsync(string gameId, string mode, Guid player, string name, int score)
    {
        await using var db = await OpenAsync();
        await using (var cmd = Command(db, """
            INSERT INTO scores (game_id, mode, player_id, name, score, created_at) VALUES (@g, @m, @p, @n, @s, @t)
            """, ("@g", gameId), ("@m", mode), ("@p", Key(player)), ("@n", name), ("@s", score), ("@t", Now())))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 自分より自己ベストが高いプレイヤーの数 + 1 = 順位
        await using var rank = Command(db, """
            SELECT COUNT(*) + 1 FROM (
                SELECT MAX(score) AS best FROM scores
                WHERE game_id = @g AND mode = @m AND player_id <> @p
                GROUP BY player_id
            ) t WHERE best > @s
            """, ("@g", gameId), ("@m", mode), ("@p", Key(player)), ("@s", score));
        return Convert.ToInt32(await rank.ExecuteScalarAsync());
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(string gameId, string mode, int limit, Guid? player)
    {
        await using var db = await OpenAsync();
        // プレイヤーごとの自己ベスト 1 行だけを取り出して並べる
        await using var cmd = Command(db, """
            SELECT player_id, name, score, created_at FROM (
                SELECT player_id, name, score, created_at,
                       ROW_NUMBER() OVER (PARTITION BY player_id ORDER BY score DESC, created_at ASC) AS rn
                FROM scores
                WHERE game_id = @g AND mode = @m
            ) t
            WHERE rn = 1
            ORDER BY score DESC, created_at ASC
            LIMIT @limit
            """, ("@g", gameId), ("@m", mode), ("@limit", Math.Clamp(limit, 1, 100)));
        await using var r = await cmd.ExecuteReaderAsync();

        var me = player is null ? null : Key(player);
        var list = new List<LeaderboardEntry>();
        int rank = 0, prevScore = int.MinValue, index = 0;
        while (await r.ReadAsync())
        {
            index++;
            var score = Convert.ToInt32(r.GetValue(2));
            if (score != prevScore) rank = index; // 同点は同順位
            prevScore = score;
            list.Add(new LeaderboardEntry(rank, r.GetString(1), score, FromMs(r.GetValue(3)), r.GetString(0) == me));
        }
        return list;
    }

    // ------------------------------------------------------------------
    private async Task<DbConnection> OpenAsync()
    {
        var db = _createConnection();
        await db.OpenAsync();
        return db;
    }

    private static DbCommand Command(DbConnection db, string sql, params (string Name, object Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task ExecAsync(DbConnection db, string sql)
    {
        await using var cmd = Command(db, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Key(Guid? player) => player!.Value.ToString("N");
    private static DateTimeOffset FromMs(object value) => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value));
    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
