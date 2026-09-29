/**
 * ゲーム画面の共通キット。
 * - 上のバー（ホームへ戻る・タイトル）を表示
 * - C# サーバーのゲームロジックを呼び出す（セッション開始・操作・得点登録）
 * - 結果ダイアログとランキング表示
 *
 * 使い方（ゲームの index.html）:
 *   <body data-game-id="memory">          ← game.json の id
 *   <script src="/00Home/js/api.js"></script>
 *   <script src="/00Home/js/gamekit.js"></script>
 *
 *   GameKit.init({ title: "神経衰弱" });
 *   const session = await GameKit.start({ mode: "normal" });
 *   const { state, events } = await session.act({ type: "flip", index: 3 });
 */
(() => {
    "use strict";

    const api = window.GameSiteApi;
    if (!api) throw new Error("gamekit.js より先に api.js を読み込んでください。");

    const script = document.currentScript;
    const HOME_URL = new URL("../homepage.html", script.src).pathname;
    const NAME_KEY = "gs:name";

    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
    }[c]));

    const gameId = () => document.body.dataset.gameId;
    const base = () => `/api/games/${encodeURIComponent(gameId())}`;

    // ---------------------------------------------------------------
    // セッション（1 回分のゲーム）
    // ---------------------------------------------------------------
    class Session {
        constructor(id, state) {
            this.id = id;
            this.state = state;
        }

        /** 操作を送る。ルール違反ならサーバーが ApiError を返す（err.data.state に最新状態）。 */
        async act(action) {
            try {
                const res = await api.post(`${base()}/sessions/${this.id}/actions`, { action });
                this.state = res.state;
                return res;
            } catch (err) {
                if (err.data?.state) this.state = err.data.state;
                throw err;
            }
        }

        /** ゲーム終了後に得点を登録する（得点はサーバーが計算済み）。 */
        submitScore(name) {
            return api.post(`${base()}/sessions/${this.id}/score`, { name });
        }
    }

    // ---------------------------------------------------------------
    // 画面部品
    // ---------------------------------------------------------------
    let toastEl, toastTimer, dialogEl;

    function init({ title } = {}) {
        const bar = document.createElement("header");
        bar.className = "gk-bar";
        bar.innerHTML = `
            <a class="gk-home" href="${esc(HOME_URL)}">
                <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 18l-6-6 6-6"/></svg>ホーム
            </a>
            <h1 class="gk-title">${esc(title || document.title)}</h1>
            <div class="gk-slot" id="gk-slot"></div>`;
        document.body.prepend(bar);

        toastEl = document.createElement("div");
        toastEl.className = "gk-toast";
        toastEl.setAttribute("role", "status");
        toastEl.setAttribute("aria-live", "polite");
        document.body.append(toastEl);

        dialogEl = document.createElement("dialog");
        dialogEl.className = "gk-dialog";
        dialogEl.setAttribute("aria-labelledby", "gk-dialog-title");
        document.body.append(dialogEl);

        // テーマ（ホームで選んだ明るさ設定を引き継ぐ）
        try {
            const t = JSON.parse(localStorage.getItem("gs:theme"));
            if (t === "light" || t === "dark") document.documentElement.dataset.theme = t;
        } catch { /* 無視 */ }

        return { slot: bar.querySelector("#gk-slot") };
    }

    function toast(msg) {
        toastEl.textContent = msg;
        toastEl.classList.add("show");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toastEl.classList.remove("show"), 2400);
    }

    const rankingHTML = (entries) => entries.length
        ? `<ol class="gk-ranking">${entries.map((e) => `
            <li class="${e.isMe ? "is-me" : ""}"><span class="rank">${e.rank}</span>
            <span class="name">${esc(e.name)}</span><span class="score">${e.score.toLocaleString()}</span></li>`).join("")}</ol>`
        : "";

    /**
     * 結果ダイアログ。名前を入れてランキング登録 → 順位とランキングを表示。
     * retryLabel … 右下のボタンの文字（既定「もう一度」）。押すと onRetry が呼ばれる。
     * message    … スコアの下に出す補足（ランキング対象外の理由など）。
     * @param {{title?:string, session:Session, details?:[string,string][], onRetry:Function, retryLabel?:string, message?:string}} opts
     */
    function showResult({ title = "クリア！", session, details = [], onRetry, retryLabel = "もう一度", message = "" }) {
        const score = session.state.score;
        let savedName = "";
        try { savedName = localStorage.getItem(NAME_KEY) || ""; } catch { /* 無視 */ }

        dialogEl.innerHTML = `
            <div class="gk-dialog-body">
                <h2 id="gk-dialog-title">${esc(title)}</h2>
                ${score != null ? `<p class="gk-score">${score.toLocaleString()}<small>点</small></p>` : ""}
                ${details.length ? `<dl class="gk-details">${details.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join("")}</dl>` : ""}
                ${message ? `<p class="gk-note">${esc(message)}</p>` : ""}
                ${score != null ? `
                <form class="gk-form" id="gk-score-form">
                    <label for="gk-name">ランキングにのせる名前</label>
                    <input id="gk-name" maxlength="12" autocomplete="nickname" enterkeyhint="send" placeholder="ななしさん" value="${esc(savedName)}">
                    <button class="gk-btn gk-btn-primary gk-btn-block" type="submit">ランキングに登録</button>
                </form>` : ""}
                <div id="gk-board"></div>
                <div class="gk-actions">
                    <a class="gk-btn" href="${esc(HOME_URL)}">ホームへ</a>
                    <button type="button" class="gk-btn gk-btn-primary" id="gk-retry">${esc(retryLabel)}</button>
                </div>
            </div>`;

        dialogEl.querySelector("#gk-retry").onclick = () => {
            dialogEl.close();
            onRetry?.();
        };

        const form = dialogEl.querySelector("#gk-score-form");
        form?.addEventListener("submit", async (e) => {
            e.preventDefault();
            const button = form.querySelector("button");
            const name = form.querySelector("input").value.trim();
            button.disabled = true;
            try {
                try { localStorage.setItem(NAME_KEY, name); } catch { /* 無視 */ }
                const res = await session.submitScore(name);
                form.outerHTML = `<p class="gk-rank-msg">${res.rank}位 にランクインしました！</p>`;
                dialogEl.querySelector("#gk-board").innerHTML = rankingHTML(res.leaderboard.entries);
            } catch (err) {
                button.disabled = false;
                toast(err.message);
            }
        });

        if (!dialogEl.open) dialogEl.showModal();
        dialogEl.querySelector("#gk-retry").focus();
    }

    /** エラー表示。時間切れ（410）なら再スタートを促す。 */
    function handleError(err, onRestart) {
        if (err?.status === 410 && onRestart) {
            dialogEl.innerHTML = `
                <div class="gk-dialog-body">
                    <h2 id="gk-dialog-title">時間切れ</h2>
                    <p>${esc(err.message)}</p>
                    <button type="button" class="gk-btn gk-btn-primary gk-btn-block" id="gk-restart">もう一度はじめる</button>
                </div>`;
            dialogEl.querySelector("#gk-restart").onclick = () => { dialogEl.close(); onRestart(); };
            if (!dialogEl.open) dialogEl.showModal();
            return;
        }
        toast(err?.message || "エラーが起きました。");
    }

    window.GameKit = {
        init,
        toast,
        showResult,
        handleError,
        homeUrl: HOME_URL,
        get gameId() { return gameId(); },

        /** 新しいゲームを開始（サーバーの C# ロジックで盤面を作る） */
        async start(options = {}) {
            const res = await api.post(`${base()}/sessions`, { options });
            return new Session(res.sessionId, res.state);
        },

        /**
         * 保存しておいたセッション ID で、プレイ中のゲームにつなぎ直す（ページを開き直したとき用）。
         * state は最初の act() の返事で入る。サーバー側で期限切れなら act() が 410 エラーになる。
         */
        attach(sessionId) {
            return new Session(sessionId, null);
        },

        leaderboard(mode, limit = 10) {
            return api.get(`${base()}/leaderboard` + api.query({ mode, limit }));
        },
    };
})();
