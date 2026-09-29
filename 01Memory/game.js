/**
 * 神経衰弱：画面の表示とタップの受け付けだけを担当する。
 * カードの並び・正誤判定・得点計算はサーバーの C#（MemoryGame.cs）が行う。
 */
(() => {
    "use strict";

    const MODE_KEY = "gs:memory:mode";
    const MISS_DELAY = 800; // ちがう絵だった時に見せておく時間（ms）

    const { slot } = GameKit.init({ title: "神経衰弱" });
    const $ = (id) => document.getElementById(id);
    const board = $("board");

    let session = null;
    let busy = false;        // ちがう絵を見せている間は操作を止める
    let timer = null;
    let startedAt = 0;
    let mode = "normal";
    try { mode = localStorage.getItem(MODE_KEY) || mode; } catch { /* 無視 */ }

    // やり直しボタン（上のバーの右）
    const restart = document.createElement("button");
    restart.className = "gk-icon-btn";
    restart.setAttribute("aria-label", "やり直す");
    restart.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12a9 9 0 1 0 3-6.7"/><path d="M3 4v5h5"/></svg>';
    restart.onclick = () => newGame();
    slot.append(restart);

    // ---------------------------------------------------------------
    async function newGame() {
        stopTimer();
        busy = false;
        board.setAttribute("aria-busy", "true");
        paintModes();
        try {
            session = await GameKit.start({ mode });
            renderBoard(session.state);
            $("hint").textContent = "カードをタップしてはじめよう";
        } catch (err) {
            GameKit.handleError(err);
            $("hint").textContent = err.message;
        } finally {
            board.setAttribute("aria-busy", "false");
        }
    }

    function renderBoard(state) {
        board.style.setProperty("--cols", state.columns);
        board.innerHTML = state.cards.map((c, i) => `
            <button type="button" class="mcard" data-index="${i}" aria-label="カード ${i + 1}">
                <span class="mcard-inner">
                    <span class="mcard-back" aria-hidden="true"></span>
                    <span class="mcard-face"></span>
                </span>
            </button>`).join("");
        state.cards.forEach((c, i) => paintCard(i, c.state, c.face));
        paintStats(state);
    }

    function cardEl(i) { return board.children[i]; }

    function paintCard(i, st, face) {
        const el = cardEl(i);
        el.classList.toggle("is-up", st === "up");
        el.classList.toggle("is-matched", st === "matched");
        el.classList.remove("is-miss");
        if (face) el.querySelector(".mcard-face").textContent = face;
        el.disabled = st === "matched";
        el.setAttribute("aria-label", st === "hidden" ? `カード ${i + 1}（うら）` : `カード ${i + 1}：${face}${st === "matched" ? "（そろった）" : ""}`);
    }

    function paintStats(state) {
        $("moves").textContent = state.moves;
        $("pairs").textContent = state.matchedPairs;
        $("total").textContent = state.pairs;
    }

    function paintModes() {
        document.querySelectorAll("#modes [data-mode]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.mode === mode)));
    }

    // ---------------------------------------------------------------
    // タイマー（表示用。得点計算に使う時間はサーバーが測る）
    // ---------------------------------------------------------------
    function startTimer() {
        if (timer) return;
        startedAt = performance.now();
        timer = setInterval(() => showTime(performance.now() - startedAt), 250);
    }
    function stopTimer() {
        clearInterval(timer);
        timer = null;
        showTime(0);
    }
    function showTime(ms) {
        const s = Math.floor(ms / 1000);
        $("time").textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
    }

    // ---------------------------------------------------------------
    // タップ
    // ---------------------------------------------------------------
    board.addEventListener("click", async (e) => {
        const el = e.target.closest(".mcard");
        if (!el || !session || busy) return;
        const i = Number(el.dataset.index);
        if (el.classList.contains("is-up") || el.classList.contains("is-matched")) return;

        startTimer();
        el.classList.add("is-up"); // 先にめくって見せ、絵柄はサーバーの返事で入れる
        busy = true;

        try {
            const { state, events } = await session.act({ type: "flip", index: i });
            busy = false;
            await playEvents(events, state);
        } catch (err) {
            busy = false;
            if (session.state?.cards) renderBoard(session.state); // サーバーの状態に合わせ直す
            GameKit.handleError(err, newGame);
        }
    });

    async function playEvents(events, state) {
        for (const ev of events) {
            if (ev.type === "flip") {
                paintCard(ev.index, "up", ev.face);
            } else if (ev.type === "match") {
                ev.indices.forEach((i) => paintCard(i, "matched", null));
                $("hint").textContent = "そろった！";
            } else if (ev.type === "mismatch") {
                busy = true;
                ev.indices.forEach((i) => cardEl(i).classList.add("is-miss"));
                $("hint").textContent = "ざんねん…";
                await wait(MISS_DELAY);
                ev.indices.forEach((i) => paintCard(i, "hidden", null));
                busy = false;
            } else if (ev.type === "finish") {
                clearInterval(timer);
                timer = null;
                showTime(ev.elapsedMs);
                $("hint").textContent = "クリア！";
                await wait(500);
                GameKit.showResult({
                    title: "クリア！",
                    session,
                    details: [["手数", `${ev.moves}回`], ["時間", $("time").textContent]],
                    onRetry: newGame,
                });
            }
        }
        paintStats(state);
    }

    const wait = (ms) => new Promise((r) => setTimeout(r, ms));

    // むずかしさの切り替え
    document.getElementById("modes").addEventListener("click", (e) => {
        const b = e.target.closest("[data-mode]");
        if (!b || b.dataset.mode === mode) return;
        mode = b.dataset.mode;
        try { localStorage.setItem(MODE_KEY, mode); } catch { /* 無視 */ }
        newGame();
    });

    newGame();
})();
