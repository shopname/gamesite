# TEMのゲームサイト

スマホで誰でもすぐ遊べるゲームサイト。
画面は **HTML / CSS / JS**、ロジックは **C#（ASP.NET Core）** で動きます。

```
ブラウザ（HTML/CSS/JS）  ──fetch──▶  C# サーバー（/api）
  表示・タップの受け付け             一覧の検索・並び替え / お気に入り / 履歴
                                    ゲームの進行・判定・得点計算 / ランキング
```

## フォルダ構成

```
gamesite/
├─ 00Home/                     ホームページ（HTML/CSS/JS）
│  ├─ homepage.html
│  ├─ css/tokens.css           色・余白などの共通値（ゲームとも共有）
│  ├─ css/base.css             ホームページの見た目
│  ├─ css/gamekit.css          ゲーム画面の共通の見た目
│  ├─ js/api.js                C# サーバーを呼ぶ共通クライアント
│  ├─ js/app.js                ホームページの表示
│  ├─ js/gamekit.js            ゲーム画面の共通キット（ホームへ戻る・結果/ランキング）
│  ├─ manifest.webmanifest     「ホーム画面に追加」用
│  └─ templates/game-template/ 新しいゲームのひな形（JS 側 + C# 側）
├─ 01Memory/                   サンプルゲーム「神経衰弱」
│  ├─ game.json                ← これがあるフォルダが自動で一覧に載る
│  ├─ index.html / style.css / game.js
└─ server/
   ├─ Dockerfile               公開用
   └─ GameSite.Server/         C# サーバー
      ├─ Program.cs
      ├─ Api/                  API（ブラウザから呼ばれる入口）
      ├─ Catalog/              game.json の読み込み・検索・並び替え
      ├─ Data/                 お気に入り・履歴・ランキング（SQLite）
      └─ Games/                ゲームのロジック
         ├─ GameLogic.cs       全ゲーム共通のしくみ
         └─ Memory/MemoryGame.cs
```

## 自分の PC で動かす

1. .NET 8 SDK を入れる（初回のみ）

   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```

2. サーバーを起動

   ```powershell
   cd server/GameSite.Server
   dotnet run
   ```

3. ブラウザで <http://localhost:5080> を開く

**スマホで確認する場合**：PC と同じ Wi-Fi につないで `http://<PCのIPアドレス>:5080` を開きます
（IP は `ipconfig` の「IPv4 アドレス」。初回は Windows ファイアウォールの許可が必要です）。

> ⚠ サーバー経由で動くので、`homepage.html` をダブルクリックで開いても動きません。

## ゲームを追加する

1. `00Home/templates/game-template/` を `gamesite/02Guess/` のようにコピー
2. `game.json` の `id` や `title` を書き換える（`id` は公開後に変えない）
3. `TemplateGame.cs` を `server/GameSite.Server/Games/Guess/` に移し、`GameId` を `game.json` の `id` と揃える
4. `index.html` の `<body data-game-id="...">` も同じ `id` にする
5. サーバーを再起動（`dotnet run`）

`game.json` を置いたフォルダは、再起動しなくても 10 秒以内に一覧へ反映されます。
C# のロジックを追加・変更した時だけ再起動が必要です。
`"status": "coming-soon"` にすると「準備中」として一覧に出し、`"hidden"` にすると一覧から隠せます。

### C# でロジックを書く

```csharp
public sealed class GuessGameLogic : IGameLogic          // 実装すると自動で登録される
{
    public string GameId => "guess";                      // game.json の id
    public GameSession CreateSession(JsonElement options, GameContext ctx) => new GuessSession(ctx.Random);
}

public sealed record GuessAction(int Number);             // ブラウザから届く操作

public sealed class GuessSession : GameSession<GuessAction>
{
    protected override ActionOutcome Handle(GuessAction a) // 操作を検証して状態を進める
    {
        ...
        IsFinished = true; FinalScore = 900;               // 終わったら得点をセット → ランキング登録できる
        return ActionOutcome.Success(new { type = "correct" });  // 何が起きたかをブラウザに伝える
    }
    public override object GetView() => new { ... };       // ブラウザに見せてよい情報だけ返す
}
```

ブラウザ側は `gamekit.js` を使います。

```js
GameKit.init({ title: "数あて" });                         // 上のバー（ホームへ戻る）
const session = await GameKit.start({ mode: "normal" });  // → CreateSession
const { state, events } = await session.act({ number: 42 }); // → Handle
GameKit.showResult({ session, onRetry: newGame });        // 結果・名前入力・ランキング
```

答えや山札などはサーバーのメモリにだけあり、ブラウザには `GetView()` の結果しか届きません。
得点もサーバーが計算するので、開発者ツールでの改ざんができません。

> リアルタイムに動くアクションゲーム（シューティングなど）は、1 操作ごとに通信すると遅延が出ます。
> その場合は動きの部分を JS で描画し、C# 側では「開始・終了・得点の妥当性チェック」だけを行う形にしてください。

## API

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/home` | ピックアップ・つづきから・タグ・件数 |
| GET | `/api/games?q=&tag=&sort=new\|popular\|name&view=all\|fav\|recent` | 一覧（検索はひらがな/カタカナ・全角/半角を区別しない） |
| GET | `/api/games/random` | おまかせ |
| GET | `/api/games/{id}` | 詳細 + ランキング上位 |
| GET | `/api/games/{id}/leaderboard?mode=` | ランキング |
| PUT / DELETE | `/api/me/favorites/{id}` | お気に入り |
| DELETE | `/api/me/history` | 履歴を消す |
| POST | `/api/games/{id}/sessions` | ゲーム開始 |
| POST | `/api/games/{id}/sessions/{sid}/actions` | 操作 |
| POST | `/api/games/{id}/sessions/{sid}/score` | 得点登録（1 プレイ 1 回） |

プレイヤーはログイン不要です。ブラウザが最初に作るランダムな ID（`X-Player-Id` ヘッダー）で見分けます。
そのため、別の端末やブラウザではお気に入り・履歴は引き継がれません。

## インターネットに公開する（Render + Neon、無料）

どこからでも（Wi-Fi でも 4G/5G でも）同じ URL で遊べるようにします。

| 役割 | サービス | 料金 |
|---|---|---|
| C# サーバーを動かす | [Render](https://render.com) | 無料プラン |
| お気に入り・履歴・ランキングを保存 | [Neon](https://neon.tech)（PostgreSQL） | 無料プラン（期限なし） |
| ソースコードを置く | [GitHub](https://github.com) | 無料 |

> Render の無料プランはサーバーのファイルが再起動のたびに消えるため、データは Neon に保存します。
> 自分の PC で `dotnet run` した時は、これまで通り SQLite ファイルに保存されます。

### 1. GitHub にアップロード
1. VS Code で `gamesite` フォルダを開く
2. 左の「ソース管理」（枝分かれのアイコン）→ **Publish to GitHub** → **Private repository** を選ぶ
3. GitHub へのログインを求められたら許可する

`.gitignore` により、ビルド結果やローカルのデータベースはアップロードされません。

### 2. Neon でデータベースを作る
1. <https://neon.tech> に GitHub アカウントでサインアップ
2. **Create project**（Region は **AWS Asia Pacific (Singapore)** が近い）
3. 表示される **Connection string**（`postgresql://...` で始まる文字列）をコピー
   - 「Pooled connection」のチェックはオンのままで OK
   - この文字列はパスワードを含むので、GitHub やチャットには貼らないでください

テーブルはサーバーの初回起動時に自動で作られます。

### 3. Render にデプロイ
1. <https://render.com> に GitHub アカウントでサインアップ
2. **New → Blueprint** → 手順 1 のリポジトリを選ぶ（`render.yaml` の設定が読み込まれます）
3. `ConnectionStrings__Postgres` の入力欄に、手順 2 でコピーした接続文字列を貼る
4. **Apply** → 数分待つと `https://tem-gamesite-xxxx.onrender.com` のような URL ができます

以後は GitHub に push するだけで自動的に更新されます
（VS Code の「ソース管理」で変更をコミット → **Sync Changes**）。

### 無料プランの注意
- 15 分アクセスがないとサーバーが眠り、次のアクセスは表示まで 30〜60 秒かかります。
  気になる場合は [UptimeRobot](https://uptimerobot.com)（無料）などで `https://<あなたのURL>/healthz` に
  5 分おきにアクセスさせると、眠らずに済みます（Render の無料枠は月 750 時間なので 1 つなら常時稼働できます）。
- サーバーが眠ったり更新されたりすると、**プレイ途中**のゲームは終了します
  （お気に入り・履歴・ランキングは Neon にあるので消えません）。

### 公開前チェック
- `server/GameSite.Server/appsettings.json` の `Site:Title` を確認
- API には 1 分あたりの回数制限が入っています（`Program.cs` の `AddRateLimiter`）
- 配信されるのは `00Home/` と `game.json` のあるフォルダだけです（`server/` などは外から見えません）
