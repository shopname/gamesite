/**
 * 数独：画面の表示と操作の受け付けを担当する。
 *
 * ■ 役割分担
 *   サーバー（SudokuGame.cs）… 出題、正解の保持、入力の正誤判定、ミス回数、経過時間、スコア
 *   このファイル            … 盤面の描画、マスの選択と強調表示、候補メモ、「元に戻す」、自動保存、アニメーション
 *   正解はサーバーから送られてこないので、ここでは「正しいか」を判定できない（判定は必ずサーバーに聞く）。
 *
 * ■ 状態の置き場所
 *   view      … サーバーが返した最新の盤面・ミス・時間など（GetView の結果）。これが正しい状態
 *   notes     … 候補メモ（マスごとに 9 ビットの整数。ビット d-1 が立っていれば候補 d）
 *   undoStack … 「元に戻す」の履歴
 *   selected  … 選択中のマス（0〜80、なしは -1）
 *
 * ■ 自動保存（localStorage）
 *   操作のたびに「セッション ID + サーバーの署名付きセーブ + メモ + 履歴」を保存する。
 *   ページを開き直したら、まず同じセッションにつなぎ直し、サーバー側で消えていたらセーブから復元する。
 */
(() => {
    "use strict";

    // ---------------------------------------------------------------
    // 定数
    // ---------------------------------------------------------------
    const SAVE_KEY = "gs:sudoku:save:v1";     // 中断したゲーム
    const DAILY_KEY = "gs:sudoku:daily:v1";   // デイリーの挑戦結果 { "2026-09-30": "clear" | "gameover" | "retired" | "playing" }
    const MOTION_KEY = "gs:sudoku:motion";    // "reduce" ならアニメーションを控えめに
    const HEARTBEAT_MS = 4 * 60 * 1000;       // プレイ中はこの間隔でサーバーに生存確認を送る（30 分の期限切れを防ぐ）
    const JST_OFFSET_MS = 9 * 60 * 60 * 1000; // デイリーの日付は日本時間で決まる（サーバーと同じ）

    const LABELS = { hard: "難しい", very_hard: "とても難しい", extreme: "極めて難しい", daily: "デイリー" };

    // ---------------------------------------------------------------
    // 盤面の形（サーバーの SudokuGrid.cs と同じ考え方）
    // ---------------------------------------------------------------
    const rowOf = (i) => Math.floor(i / 9);
    const colOf = (i) => i % 9;
    const boxOf = (i) => Math.floor(rowOf(i) / 3) * 3 + Math.floor(colOf(i) / 3);
    const bit = (d) => 1 << (d - 1);

    /** 各マスのピア（同じ行・列・ブロックの 20 マス）。最初に 1 回だけ計算しておく */
    const PEERS = Array.from({ length: 81 }, (_, i) =>
        Array.from({ length: 81 }, (_, j) => j).filter((j) =>
            j !== i && (rowOf(j) === rowOf(i) || colOf(j) === colOf(i) || boxOf(j) === boxOf(i))));

    /** ユニット（行・列・ブロック）に属するマス。サーバーの「埋まったユニット」イベントの演出に使う */
    const UNIT_CELLS = {
        row: (n) => Array.from({ length: 9 }, (_, k) => n * 9 + k),
        col: (n) => Array.from({ length: 9 }, (_, k) => k * 9 + n),
        box: (n) => Array.from({ length: 9 }, (_, k) => (Math.floor(n / 3) * 3 + Math.floor(k / 3)) * 9 + (n % 3) * 3 + (k % 3)),
    };

    // ---------------------------------------------------------------
    // 画面の部品
    // ---------------------------------------------------------------
    const { slot } = GameKit.init({ title: "数独" });
    const $ = (id) => document.getElementById(id);
    const app = $("app");
    const board = $("board");
    const numpad = $("numpad");
    const pauseDialog = $("pause-dialog");
    const confirmDialog = $("confirm-dialog");
    const rankingDialog = $("ranking-dialog");

    // 一時停止ボタンは上のバーの右端（gk-slot）に置く。プレイ中だけ表示する
    const pauseBtn = document.createElement("button");
    pauseBtn.type = "button";
    pauseBtn.id = "pause";
    pauseBtn.className = "gk-icon-btn";
    pauseBtn.setAttribute("aria-label", "一時停止");
    pauseBtn.hidden = true;
    pauseBtn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 5v14M15 5v14"/></svg>';
    slot.append(pauseBtn);

    // ---------------------------------------------------------------
    // 状態
    // ---------------------------------------------------------------
    let session = null;        // GameKit のセッション（サーバーとの通信口）
    let view = null;           // サーバーの最新の状態
    let notes = new Array(81).fill(0);
    let undoStack = [];
    let selected = -1;
    let memoMode = false;
    let playing = false;       // プレイ画面で、ゲームが続いている間 true
    const pendingCells = new Set(); // サーバーの返事待ちのマス（二重入力を防ぐ）

    // 時計：サーバーの経過時間（elapsedMs）を受け取った時刻から、手元で進めて表示する
    const clock = { baseMs: 0, at: 0, running: false };
    let tickTimer = 0;
    let heartbeatTimer = 0;

    // 操作は 1 つずつ順番にサーバーへ送る（速く連打しても順序が入れ替わらないように）
    let queue = Promise.resolve();
    const enqueue = (task) => {
        const run = queue.then(task);
        queue = run.catch(() => {});
        return run;
    };

    // アニメーションを控えめにする設定（OS の「視差効果を減らす」も尊重する）
    const osReduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    let reduceMotion = false;
    try { reduceMotion = localStorage.getItem(MOTION_KEY) === "reduce"; } catch { /* 使えない環境では既定値 */ }
    const motionOff = () => reduceMotion || osReduce.matches;
    const wait = (ms) => new Promise((r) => setTimeout(r, motionOff() ? 0 : ms));

    // ===============================================================
    // 保存（localStorage）
    // ===============================================================
    function readJson(key) {
        try { return JSON.parse(localStorage.getItem(key)); } catch { return null; }
    }
    function writeJson(key, value) {
        try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* 容量不足・プライベートモードなどでは保存しない */ }
    }

    /** 今のゲームを保存する。操作のたびに呼ぶ（仕様「自動保存」） */
    function save() {
        if (!session || !view || view.finished || !view.save) return;
        writeJson(SAVE_KEY, {
            sessionId: session.id,
            save: view.save,          // サーバーの署名付きセーブ（盤面・ミス・時間）
            notes,                    // 候補メモ（ブラウザだけが持つ）
            undo: undoStack.slice(-300), // 履歴は直近 300 件まで（保存容量の節約）
            selected,
            memo: memoMode,
            summary: {                // 難易度選択画面の「つづきから」に出す情報
                mode: view.daily ? "daily" : view.difficulty,
                daily: view.daily,
                progress: view.progress,
                elapsedMs: elapsedNow(),
                mistakes: view.mistakes,
            },
        });
    }
    const loadSave = () => readJson(SAVE_KEY);
    function clearSave() {
        try { localStorage.removeItem(SAVE_KEY); } catch { /* 無視 */ }
    }

    /** デイリーの挑戦結果を記録する（難易度選択画面の「クリア済み」などの表示用） */
    function markDaily(date, status) {
        if (!date) return;
        const all = readJson(DAILY_KEY) || {};
        // 1 週間より古い記録は捨てる
        for (const d of Object.keys(all)) if (d < shiftDate(todayJst(), -7)) delete all[d];
        // 「クリア」を後から「挑戦中」などで上書きしない
        if (all[date] !== "clear") all[date] = status;
        writeJson(DAILY_KEY, all);
    }

    /** 日本時間の今日（"2026-09-30"）。サーバーのデイリー切り替えと同じ基準 */
    function todayJst() {
        return new Date(Date.now() + JST_OFFSET_MS).toISOString().slice(0, 10);
    }
    function shiftDate(iso, days) {
        const d = new Date(`${iso}T00:00:00Z`);
        d.setUTCDate(d.getUTCDate() + days);
        return d.toISOString().slice(0, 10);
    }

    // ===============================================================
    // 画面の切り替え
    // ===============================================================
    function showScreen(name) {
        app.dataset.screen = name;
        $("select-screen").hidden = name !== "select";
        $("play-screen").hidden = name !== "play";
        pauseBtn.hidden = name !== "play";
        window.scrollTo({ top: 0, behavior: motionOff() ? "auto" : "smooth" });
    }

    /** 難易度選択画面を表示する（「つづきから」とデイリーの状態も更新） */
    function showSelect() {
        stopPlaying();
        session = null;
        view = null;
        showScreen("select");

        const saved = loadSave();
        const cont = $("continue");
        cont.hidden = !saved;
        if (saved) {
            const s = saved.summary || {};
            const label = s.daily ? `デイリー ${formatDate(s.daily)}` : LABELS[s.mode] || "";
            $("continue-meta").textContent = `${label} ・ 進捗 ${s.progress ?? 0}% ・ ${formatTime(s.elapsedMs ?? 0)} ・ ミス ${s.mistakes ?? 0}`;
            $("continue-bar").style.width = `${s.progress ?? 0}%`;
        }

        const today = todayJst();
        $("daily-date").textContent = formatDate(today);
        const status = (readJson(DAILY_KEY) || {})[today];
        const playingDaily = saved?.summary?.daily === today;
        $("daily-status").textContent = playingDaily ? "挑戦中"
            : status === "clear" ? "クリア済み"
            : status ? "挑戦済み（ランキング対象外）"
            : "未挑戦";
        $("daily").dataset.status = playingDaily ? "playing" : status || "new";
    }

    // ===============================================================
    // ゲームの開始・再開
    // ===============================================================

    /** 新しいゲームを始める。中断中のゲームがあれば、破棄してよいか確認する */
    async function startGame(mode) {
        const saved = loadSave();
        if (saved) {
            const ok = await confirmAction({
                title: "新しく始めますか？",
                desc: "中断中のゲームは失われます。",
                ok: "新しく始める",
            });
            if (!ok) return;
            clearSave();
        }

        prepareBoard();
        showScreen("play");
        board.setAttribute("aria-busy", "true");
        app.classList.add("is-loading");
        try {
            session = await GameKit.start({ mode });
            notes = new Array(81).fill(0);
            undoStack = [];
            selected = -1;
            setMemo(false);
            applyState(session.state);
            if (view.daily) markDaily(view.daily, "playing");
            beginPlaying({ intro: true });
        } catch (err) {
            GameKit.toast(err.message || "ゲームを始められませんでした。");
            showSelect();
        } finally {
            app.classList.remove("is-loading");
        }
    }

    /**
     * 中断したゲームを再開する。
     * 1. 保存したセッション ID でサーバーにつなぎ直す（サーバーのメモリにまだ残っていれば、そのまま続けられる）
     * 2. 期限切れ（410）なら、署名付きセーブから新しいセッションとして復元する
     */
    async function continueGame() {
        const saved = loadSave();
        if (!saved) return showSelect();

        prepareBoard();
        showScreen("play");
        app.classList.add("is-loading");
        notes = Array.isArray(saved.notes) && saved.notes.length === 81 ? saved.notes : new Array(81).fill(0);
        undoStack = Array.isArray(saved.undo) ? saved.undo : [];
        selected = Number.isInteger(saved.selected) ? saved.selected : -1;
        setMemo(!!saved.memo);

        try {
            try {
                session = GameKit.attach(saved.sessionId);
                applyState((await session.act({ type: "sync" })).state);
            } catch (err) {
                if (err.status !== 410 && err.status !== 400 && err.status !== 404) throw err;
                if (!(await restoreFrom(saved.save))) return;
            }
            if (view.finished) {
                // 別の画面ですでに終わっていた
                clearSave();
                GameKit.toast("このゲームはすでに終了しています。");
                return showSelect();
            }
            if (view.paused) applyState((await session.act({ type: "resume" })).state);
            beginPlaying({ intro: true });
        } catch (err) {
            GameKit.toast(err.message || "再開できませんでした。");
            showSelect();
        } finally {
            app.classList.remove("is-loading");
        }
    }

    /** 署名付きセーブから復元する。失敗したらセーブを消して難易度選択へ戻る */
    async function restoreFrom(token) {
        session = await GameKit.start({ restore: token });
        if (session.state?.restoreFailed) {
            clearSave();
            GameKit.toast("続きのデータを復元できませんでした。");
            showSelect();
            return false;
        }
        applyState(session.state);
        return true;
    }

    /** プレイ開始（時計・生存確認・描画） */
    function beginPlaying({ intro = false } = {}) {
        playing = true;
        paintAll();
        board.setAttribute("aria-busy", "false");
        if (intro) playIntro();
        startTicking();
        clearInterval(heartbeatTimer);
        heartbeatTimer = setInterval(() => {
            if (playing && !view?.paused && document.visibilityState === "visible") act({ type: "sync" }).then(save).catch(() => {});
        }, HEARTBEAT_MS);
        save();
        if (selected >= 0) focusCell(selected, false);
    }

    function stopPlaying() {
        playing = false;
        clearInterval(tickTimer);
        clearInterval(heartbeatTimer);
        pendingCells.clear();
        if (pauseDialog.open) pauseDialog.close();
    }

    // ===============================================================
    // サーバーとのやりとり
    // ===============================================================

    /** サーバーの返事（state）を取り込む */
    function applyState(state) {
        if (!state) return;
        view = state;
        clock.baseMs = state.elapsedMs ?? 0;
        clock.at = performance.now();
        clock.running = !state.paused && !state.finished;
    }

    /**
     * 操作を送る。サーバー側でセッションが期限切れ（410）なら、セーブから復元してもう一度だけ送り直す。
     * 失敗したときは、サーバーが返した最新の状態（err.data.state）に合わせてから例外を投げ直す。
     */
    async function act(action, { retry = true } = {}) {
        try {
            const res = await session.act(action);
            applyState(res.state);
            return res;
        } catch (err) {
            if (err.status === 410 && retry && view?.save) {
                if (await restoreFrom(view.save)) return act(action, { retry: false });
            }
            if (err.data?.state) applyState(err.data.state);
            throw err;
        }
    }

    /** 操作の失敗をプレイヤーに伝える */
    function reportError(err) {
        if (err?.message?.includes("別の画面で再開")) {
            GameKit.toast("このゲームは別の画面で再開されています。");
            return showSelect();
        }
        GameKit.toast(err?.message || "エラーが起きました。");
        paintAll();
    }

    /**
     * ページを閉じる・隠れるときの一時停止。
     * 普通の通信はページを閉じると途中で打ち切られるので、keepalive を付けて最後まで送り届ける。
     */
    function pauseInBackground() {
        if (!playing || !session || view?.paused) return;
        clock.baseMs = elapsedNow();
        clock.running = false;
        if (view) view.paused = true;
        try {
            fetch(`/api/games/sudoku/sessions/${encodeURIComponent(session.id)}/actions`, {
                method: "POST",
                keepalive: true,
                headers: { "Content-Type": "application/json", "X-Player-Id": GameSiteApi.playerId() },
                body: JSON.stringify({ action: { type: "pause" } }),
            })
                .then((r) => (r.ok ? r.json() : null))
                .then((res) => { if (res?.state) { applyState(res.state); save(); } })
                .catch(() => {});
        } catch { /* 送れなくても、次に開いたときにサーバーの状態へ合わせ直す */ }
        save();
    }

    // ===============================================================
    // 盤面の組み立てと描画
    // ===============================================================

    /** 81 マスの要素を作る（最初の 1 回だけ） */
    function prepareBoard() {
        if (board.childElementCount) return;
        const rows = [];
        for (let r = 0; r < 9; r++) {
            const cells = [];
            for (let c = 0; c < 9; c++) {
                const i = r * 9 + c;
                // 3×3 の境目に太線を引くためのクラス
                const edge = `${c % 3 === 2 && c < 8 ? " edge-r" : ""}${r % 3 === 2 && r < 8 ? " edge-b" : ""}`;
                cells.push(`<div class="cell${edge}" role="gridcell" data-cell="${i}" tabindex="-1" style="--r:${r};--c:${c}">
                    <span class="val"></span>
                    <span class="notes" aria-hidden="true">${[1, 2, 3, 4, 5, 6, 7, 8, 9].map((d) => `<i data-n="${d}"></i>`).join("")}</span>
                </div>`);
            }
            rows.push(`<div class="row" role="row">${cells.join("")}</div>`);
        }
        board.innerHTML = rows.join("");
    }

    const cellEl = (i) => board.querySelector(`[data-cell="${i}"]`);
    const valueAt = (i) => Number(view?.cells?.[i] ?? 0);
    const isGiven = (i) => view?.givens?.[i] !== "0";

    /** 数字 d の残数 = 9 − 盤面にある個数（初期数字 + 正しく確定した数字）。候補メモは数えない */
    function remaining(d) {
        let n = 0;
        for (let i = 0; i < 81; i++) if (valueAt(i) === d) n++;
        return 9 - n;
    }

    /** 盤面・数字ボタン・統計をまとめて描き直す */
    function paintAll() {
        paintBoard();
        paintNumpad();
        paintStats();
    }

    /**
     * 盤面の描画。強調表示のルール（モノトーンでも区別できるよう、色の濃さではなく「形」で分ける）:
     *   選択中のマス   … 白黒反転（いちばん強い）
     *   同じ行・列・枠 … 薄い網かけ
     *   同じ数字       … 数字の周りに丸い枠 ＋ 太字
     *   同じ数字のメモ … 小さな数字を反転
     */
    function paintBoard() {
        if (!view) return;
        const selValue = selected >= 0 ? valueAt(selected) : 0;
        const peers = selected >= 0 ? new Set(PEERS[selected]) : new Set();

        for (let i = 0; i < 81; i++) {
            const el = cellEl(i);
            const v = valueAt(i);
            const given = isGiven(i);
            el.classList.toggle("is-given", given);
            el.classList.toggle("is-filled", v !== 0 && !given);
            el.classList.toggle("is-selected", i === selected);
            el.classList.toggle("is-peer", peers.has(i));
            el.classList.toggle("is-same", selValue !== 0 && v === selValue && i !== selected);
            el.classList.toggle("is-pending", pendingCells.has(i));
            el.tabIndex = i === (selected >= 0 ? selected : 0) ? 0 : -1; // 矢印キーで動く「ロービング tabindex」

            if (!pendingCells.has(i)) el.querySelector(".val").textContent = v ? String(v) : "";

            // 候補メモ（数字が入っていないマスだけ表示）
            const mask = v ? 0 : notes[i];
            el.querySelectorAll(".notes i").forEach((n, k) => {
                const d = k + 1;
                const on = (mask & bit(d)) !== 0;
                n.textContent = on ? String(d) : "";
                n.classList.toggle("is-hit", on && d === selValue);
            });

            el.setAttribute("aria-label", cellLabel(i, v, given, mask));
            el.setAttribute("aria-selected", String(i === selected));
        }

        $("play-coord").textContent = selected >= 0 ? `R${rowOf(selected) + 1} · C${colOf(selected) + 1}` : "R– · C–";
    }

    /** 画面読み上げ用のマスの説明（例:「5行3列、7、初期数字」「2行8列、空き、候補 1 4 9」） */
    function cellLabel(i, v, given, mask) {
        const pos = `${rowOf(i) + 1}行${colOf(i) + 1}列`;
        if (v) return `${pos}、${v}${given ? "、初期数字" : ""}`;
        const cands = [1, 2, 3, 4, 5, 6, 7, 8, 9].filter((d) => mask & bit(d));
        return `${pos}、空き${cands.length ? `、候補 ${cands.join(" ")}` : ""}`;
    }

    /** 数字ボタン：残数の表示と、残数 0 のときの無効化（確定入力のときだけ無効。メモ入力は可能） */
    function paintNumpad() {
        if (!view) return;
        numpad.classList.toggle("is-memo", memoMode);
        numpad.querySelectorAll("[data-number]").forEach((b) => {
            const d = Number(b.dataset.number);
            const left = remaining(d);
            b.querySelector(".left").textContent = left ? String(left) : "完";
            b.classList.toggle("is-done", left === 0);
            b.disabled = !memoMode && left === 0;
            b.setAttribute("aria-label", `${d}（残り ${left}）`);
        });
    }

    function paintStats() {
        if (!view) return;
        $("play-mode").textContent = view.daily ? `デイリー ${formatDate(view.daily)} ・ ${LABELS.extreme}` : view.label;
        $("mistakes").textContent = view.mistakes;
        $("progress").textContent = `${view.progress}%`;
        $("progress-line").style.width = `${view.progress}%`;
        $("stat-mistakes").dataset.level = String(view.mistakes); // ミスが増えるほど強調（CSS）
        showTime();
    }

    // ---------------------------------------------------------------
    // 時計
    // ---------------------------------------------------------------
    const elapsedNow = () => clock.baseMs + (clock.running ? performance.now() - clock.at : 0);

    function formatTime(ms) {
        const s = Math.floor(ms / 1000);
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        const sec = String(s % 60).padStart(2, "0");
        return h ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
    }

    function formatDate(iso) {
        const [, m, d] = iso.split("-").map(Number);
        return `${m}月${d}日`;
    }

    function showTime() {
        const text = formatTime(elapsedNow());
        $("time").textContent = text;
        $("pause-time").textContent = text;
    }

    function startTicking() {
        clearInterval(tickTimer);
        showTime();
        tickTimer = setInterval(showTime, 250);
    }

    // ===============================================================
    // 入力
    // ===============================================================

    /** マスを選ぶ */
    function select(i, { focus = true } = {}) {
        if (!playing || i < 0 || i > 80) return;
        selected = i;
        paintBoard();
        if (focus) focusCell(i);
        save();
    }

    function focusCell(i, scroll = true) {
        cellEl(i)?.focus({ preventScroll: !scroll });
    }

    /** 数字ボタン・キーボードからの入力。メモモード（または Shift）なら候補メモ、そうでなければ確定入力 */
    function input(d, asNote = false) {
        if (!playing || view?.paused) return;
        if (selected < 0) {
            GameKit.toast("先にマスを選んでください。");
            return;
        }
        if (valueAt(selected) || pendingCells.has(selected)) {
            // 数字が入っているマスでは入力しない（その数字の強調表示だけ見せる）
            nudge(cellEl(selected));
            return;
        }
        if (memoMode || asNote) toggleNote(selected, d);
        else place(selected, d);
    }

    /** 候補メモの切り替え（ブラウザだけで完結。サーバーには送らない） */
    function toggleNote(i, d) {
        const before = notes[i];
        const after = before ^ bit(d);
        notes[i] = after;
        undoStack.push({ t: "note", c: i, before, after });
        paintBoard();
        const n = cellEl(i).querySelector(`.notes i[data-n="${d}"]`);
        animate(n, after & bit(d) ? "note-in" : "note-out");
        save();
    }

    /** 選択中のマスの候補メモを全部消す */
    function eraseNotes() {
        if (!playing || view?.paused || selected < 0) return;
        if (!notes[selected] || valueAt(selected)) {
            nudge(cellEl(selected));
            return;
        }
        undoStack.push({ t: "note", c: selected, before: notes[selected], after: 0 });
        notes[selected] = 0;
        paintBoard();
        save();
    }

    /**
     * 数字の確定入力。サーバーがその場で正誤判定する。
     *   正解 … 確定し、同じ行・列・ブロックの候補メモから同じ数字を自動で消す
     *   誤答 … ミスを 1 回数え、盤面には残さない（3 回目でゲームオーバー）
     */
    function place(i, d) {
        if (remaining(d) === 0) {
            nudge(numpad.querySelector(`[data-number="${d}"]`));
            return;
        }
        pendingCells.add(i);
        const el = cellEl(i);
        el.querySelector(".val").textContent = String(d); // 返事を待つ間、薄く表示しておく
        el.classList.add("is-pending");

        enqueue(async () => {
            let res;
            try {
                res = await act({ type: "place", cell: i, digit: d });
            } catch (err) {
                pendingCells.delete(i);
                return reportError(err);
            }
            pendingCells.delete(i);
            await playEvents(res.events);
        });
    }

    /** サーバーから届いた出来事を、順番に画面へ反映する */
    async function playEvents(events) {
        for (const ev of events) {
            if (ev.type === "placed") onPlaced(ev);
            else if (ev.type === "mistake") onMistake(ev);
            else if (ev.type === "unplaced") paintAll();
            else if (ev.type === "gameover") return finish("gameover");
            else if (ev.type === "clear") return finish("clear");
        }
        paintAll();
        save();
    }

    function onPlaced({ cell, digit, units, digitDone }) {
        // 候補メモの自動消去。消した内容は「元に戻す」で一緒に戻せるよう、履歴に入れておく
        const removed = [];
        if (notes[cell]) {
            removed.push([cell, notes[cell]]);
            notes[cell] = 0;
        }
        for (const p of PEERS[cell]) {
            if (notes[p] & bit(digit)) {
                removed.push([p, notes[p]]);
                notes[p] &= ~bit(digit);
            }
        }
        undoStack.push({ t: "place", c: cell, d: digit, notes: removed });

        paintAll();
        const el = cellEl(cell);
        animate(el, "stamp");
        for (const [p] of removed) if (p !== cell) animate(cellEl(p), "notes-wipe");

        // 行・列・ブロックが埋まったら、そのマスを波のように光らせる
        for (const u of units) {
            UNIT_CELLS[u.kind](u.index).forEach((c, k) => animate(cellEl(c), "sweep", { "--d": `${k * 35}ms` }));
        }
        if (digitDone) animate(numpad.querySelector(`[data-number="${digit}"]`), "done");
        announce(`${rowOf(cell) + 1}行${colOf(cell) + 1}列に ${digit} を確定${units.length ? `。${units.map(unitName).join("と")}が完成` : ""}`);
    }

    const unitName = (u) => `${u.index + 1}${u.kind === "row" ? "行目" : u.kind === "col" ? "列目" : "番目のブロック"}`;

    function onMistake({ cell, digit, mistakes, maxMistakes }) {
        const el = cellEl(cell);
        el.dataset.wrong = String(digit);
        animate(el, "wrong").then(() => delete el.dataset.wrong);
        animate($("stat-mistakes"), "alarm");
        animate(app, "flash");
        navigator.vibrate?.(80);
        const left = maxMistakes - mistakes;
        announce(`${digit} はまちがいです。ミス ${mistakes} 回目${left > 0 ? `、あと ${left} 回で終了` : ""}`);
        if (left === 1) GameKit.toast("あと 1 回まちがえると終了です。");
    }

    /**
     * 元に戻す。
     *   メモの変更 … その場で元に戻す
     *   確定入力   … サーバーに取り消しを送り、自動で消えた候補メモも一緒に戻す（ミス回数は戻らない）
     */
    function undo() {
        if (!playing || view?.paused) return;
        const last = undoStack.at(-1);
        if (!last) {
            GameKit.toast("元に戻せる操作がありません。");
            return;
        }
        if (last.t === "note") {
            undoStack.pop();
            notes[last.c] = last.before;
            select(last.c, { focus: false });
            animate(cellEl(last.c), "rewind");
            save();
            return;
        }
        enqueue(async () => {
            try {
                await act({ type: "unplace", cell: last.c });
            } catch (err) {
                // 盤面にもうその数字がない（別の画面で操作された等）なら、履歴だけ捨てる
                if (err.status === 400 && valueAt(last.c) === 0) undoStack.pop();
                return reportError(err);
            }
            undoStack.pop();
            for (const [c, mask] of last.notes) notes[c] = mask;
            selected = last.c;
            paintAll();
            animate(cellEl(last.c), "rewind");
            announce(`${rowOf(last.c) + 1}行${colOf(last.c) + 1}列の ${last.d} を取り消しました`);
            save();
        });
    }

    /** 数字／メモの切り替え */
    function setMemo(on) {
        memoMode = on;
        $("memo").setAttribute("aria-pressed", String(on));
        $("memo-state").textContent = on ? "ON" : "OFF";
        $("input-mode").innerHTML = `入力：<b>${on ? "メモ（候補）" : "数字"}</b>`;
        board.classList.toggle("is-memo", on);
        paintNumpad();
    }

    // ===============================================================
    // 一時停止・リセット・リタイア
    // ===============================================================

    /** 一時停止：時計を止め、盤面を隠して「再開」「リセット」「リタイア」を出す */
    function pause() {
        if (!playing) return;
        clock.baseMs = elapsedNow();
        clock.running = false;
        app.classList.add("is-paused");
        showTime();
        if (!pauseDialog.open) pauseDialog.showModal();
        $("resume").focus();
        if (!view?.paused) {
            enqueue(() => act({ type: "pause" }).then(save).catch(reportError));
        }
    }

    function resume() {
        if (pauseDialog.open) pauseDialog.close();
        app.classList.remove("is-paused");
        enqueue(async () => {
            try {
                await act({ type: "resume" });
            } catch (err) {
                return reportError(err);
            }
            save();
            if (selected >= 0) focusCell(selected, false);
        });
    }

    /** リセット：同じ問題を最初から（入力・候補メモ・時間・ミスを初期化） */
    async function reset() {
        const ok = await confirmAction({
            title: "最初からやり直しますか？",
            desc: "入力した数字・候補メモ・時間・ミス回数がすべて消えます。リセットしたプレイはランキングの対象外になります。",
            ok: "リセットする",
        });
        if (!ok) return;
        enqueue(async () => {
            try {
                await act({ type: "reset" });
            } catch (err) {
                return reportError(err);
            }
            notes = new Array(81).fill(0);
            undoStack = [];
            selected = -1;
            if (pauseDialog.open) pauseDialog.close();
            app.classList.remove("is-paused");
            paintAll();
            playIntro();
            save();
            GameKit.toast("最初からやり直します。");
        });
    }

    /** リタイア：確認してから終了し、難易度選択へ戻る（スコア・ランキングには載らない） */
    async function retire() {
        const ok = await confirmAction({
            title: "リタイアしますか？",
            desc: "このゲームは終了し、記録は残りません。",
            ok: "リタイアする",
        });
        if (!ok) return;
        enqueue(async () => {
            try {
                await act({ type: "retire" });
            } catch (err) {
                // 通信できなくても、この画面ではゲームを終わらせる（サーバー側は 30 分で自然に消える）
                if (err.status !== 0 && err.status !== 410) return reportError(err);
            }
            if (view?.daily) markDaily(view.daily, "retired");
            clearSave();
            app.classList.remove("is-paused");
            showSelect();
        });
    }

    /**
     * 確認ダイアログ。押されたボタンに応じて true / false を返す Promise。
     * 一時停止ダイアログの上に重ねて出せる（<dialog> は後から開いたものが手前に来る）。
     */
    function confirmAction({ title, desc, ok }) {
        $("confirm-title").textContent = title;
        $("confirm-desc").textContent = desc;
        $("confirm-ok").textContent = ok;
        confirmDialog.showModal();
        $("confirm-cancel").focus(); // 取り消せない操作なので、うっかり Enter で実行されないよう「やめる」を選んでおく
        return new Promise((resolve) => {
            const done = (value) => {
                confirmDialog.close();
                $("confirm-ok").onclick = $("confirm-cancel").onclick = confirmDialog.oncancel = null;
                resolve(value);
            };
            $("confirm-ok").onclick = () => done(true);
            $("confirm-cancel").onclick = () => done(false);
            confirmDialog.oncancel = (e) => { e.preventDefault(); done(false); };
        });
    }

    // ===============================================================
    // 終了（クリア・ゲームオーバー）
    // ===============================================================
    async function finish(kind) {
        const state = view;
        stopPlaying();
        clearSave();
        paintAll();
        showTime();
        if (state.daily) markDaily(state.daily, kind);

        const details = [
            ["難易度", state.daily ? `デイリー ${formatDate(state.daily)}` : state.label],
            ["時間", formatTime(state.elapsedMs)],
            ["ミス", `${state.mistakes}回`],
        ];

        if (kind === "clear") {
            await playClear();
            const message = state.score != null
                ? `この問題に必要だった技法：${state.technique}`
                : `ランキング対象外（${state.unrankedReason || "条件外"}）・ 参考スコア ${(state.points ?? 0).toLocaleString()} 点`;
            GameKit.showResult({ title: "クリア", session, details, message, retryLabel: "戻る", onRetry: showSelect });
        } else {
            await playGameOver();
            details[2] = ["進捗", `${state.progress}%`];
            GameKit.showResult({
                title: "ゲームオーバー",
                session,
                details,
                message: "3 回まちがえたため終了しました。",
                retryLabel: "難易度選択へ戻る",
                onRetry: showSelect,
            });
        }
    }

    // ===============================================================
    // アニメーション
    // ===============================================================

    /**
     * 要素にアニメーション用のクラスを付け、終わったら外す。終わりを待てる Promise を返す。
     * vars で CSS 変数（遅延時間など）を渡せる。
     */
    function animate(el, name, vars = {}) {
        if (!el) return Promise.resolve();
        if (motionOff()) return Promise.resolve();
        for (const [k, v] of Object.entries(vars)) el.style.setProperty(k, v);
        el.classList.remove(`fx-${name}`);
        void el.offsetWidth; // いったん描画させて、同じアニメーションを連続でも再生できるようにする
        el.classList.add(`fx-${name}`);
        return new Promise((resolve) => {
            const end = () => {
                el.classList.remove(`fx-${name}`);
                resolve();
            };
            el.addEventListener("animationend", end, { once: true });
            setTimeout(end, 1600); // animationend が来ない場合の保険
        });
    }

    /** 入力できない操作をしたときの小さな揺れ */
    const nudge = (el) => animate(el, "nudge");

    /** 盤面の登場：中央から外側へ、墨がにじむように数字が現れる */
    function playIntro() {
        board.classList.remove("fx-scan");
        if (motionOff()) return;
        for (let i = 0; i < 81; i++) {
            const dist = Math.hypot(rowOf(i) - 4, colOf(i) - 4);
            animate(cellEl(i), "intro", { "--d": `${Math.round(dist * 45)}ms` });
        }
        animate(board, "scan");
    }

    /** クリア：最後のマスから波紋が広がり、盤面全体が反転して戻る */
    async function playClear() {
        const origin = undoStack.at(-1)?.c ?? 40;
        for (let i = 0; i < 81; i++) {
            const dist = Math.hypot(rowOf(i) - rowOf(origin), colOf(i) - colOf(origin));
            animate(cellEl(i), "clear", { "--d": `${Math.round(dist * 55)}ms` });
        }
        animate(board, "cleared");
        announce("クリアしました。");
        await wait(1300);
    }

    /** ゲームオーバー：盤面が沈むように色を失う */
    async function playGameOver() {
        animate(board, "gameover");
        announce("3 回まちがえたため、ゲームオーバーです。");
        await wait(900);
    }

    /** 画面読み上げソフトへのお知らせ */
    function announce(text) {
        const el = $("announcer");
        el.textContent = "";
        requestAnimationFrame(() => { el.textContent = text; });
    }

    function applyMotionSetting() {
        document.documentElement.dataset.motion = reduceMotion ? "reduce" : "full";
        $("reduce-motion").checked = reduceMotion;
    }

    // ===============================================================
    // ランキング
    // ===============================================================
    let rankingBoard = "daily";

    async function openRanking(boardId = rankingBoard) {
        rankingBoard = boardId;
        $("ranking-tabs").querySelectorAll("[data-board]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.board === boardId)));
        if (!rankingDialog.open) rankingDialog.showModal();

        const list = $("ranking-list");
        const mode = boardId === "daily" ? `daily-${todayJst()}` : boardId;
        list.innerHTML = `<p class="ranking-empty">読み込み中…</p>`;
        try {
            const res = await GameKit.leaderboard(mode, 20);
            if (rankingBoard !== boardId) return; // 読み込み中に別のタブが押された
            const heading = boardId === "daily" ? `<p class="ranking-caption">${formatDate(todayJst())} の盤面</p>` : "";
            list.innerHTML = heading + (res.entries.length
                ? `<ol class="gk-ranking">${res.entries.map((e) => `
                    <li class="${e.isMe ? "is-me" : ""}"><span class="rank">${e.rank}</span>
                    <span class="name">${escapeHtml(e.name)}${e.isMe ? "（あなた）" : ""}</span>
                    <span class="score">${e.score.toLocaleString()}</span></li>`).join("")}</ol>`
                : `<p class="ranking-empty">まだ記録がありません。一番乗りを目指しましょう。</p>`);
        } catch (err) {
            list.innerHTML = `<p class="ranking-empty">${escapeHtml(err.message)}</p>`;
        }
    }

    const escapeHtml = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
    }[c]));

    // ===============================================================
    // イベントの登録
    // ===============================================================

    // 難易度選択
    $("modes").addEventListener("click", (e) => {
        const b = e.target.closest("[data-mode]");
        if (b) startGame(b.dataset.mode);
    });
    $("daily").addEventListener("click", () => {
        const saved = loadSave();
        // 今日のデイリーを中断していたら、新しく始めずに続きを開く
        if (saved?.summary?.daily === todayJst()) continueGame();
        else startGame("daily");
    });
    $("continue").addEventListener("click", continueGame);
    $("open-ranking").addEventListener("click", () => openRanking());
    $("ranking-tabs").addEventListener("click", (e) => {
        const b = e.target.closest("[data-board]");
        if (b) openRanking(b.dataset.board);
    });
    $("ranking-close").addEventListener("click", () => rankingDialog.close());

    // 盤面：タップ・クリックでマスを選ぶ
    board.addEventListener("pointerdown", (e) => {
        const el = e.target.closest(".cell");
        if (!el) return;
        e.preventDefault(); // フォーカスの移動による画面のスクロールを防ぐ（focus は select() で行う）
        select(Number(el.dataset.cell));
    });

    // 数字ボタン
    numpad.addEventListener("click", (e) => {
        const b = e.target.closest("[data-number]");
        if (b) input(Number(b.dataset.number));
    });

    $("memo").addEventListener("click", () => {
        setMemo(!memoMode);
        save();
    });
    $("undo").addEventListener("click", undo);
    $("erase").addEventListener("click", eraseNotes);

    // 一時停止まわり
    pauseBtn.addEventListener("click", pause);
    $("resume").addEventListener("click", resume);
    $("reset").addEventListener("click", reset);
    $("retire").addEventListener("click", retire);
    // Esc で一時停止ダイアログが閉じられたら「再開」として扱う（閉じただけで盤面が隠れたままにならないように）
    pauseDialog.addEventListener("cancel", (e) => {
        e.preventDefault();
        resume();
    });
    $("reduce-motion").addEventListener("change", (e) => {
        reduceMotion = e.target.checked;
        try { localStorage.setItem(MOTION_KEY, reduceMotion ? "reduce" : "full"); } catch { /* 無視 */ }
        applyMotionSetting();
    });

    // キーボード操作
    document.addEventListener("keydown", (e) => {
        if (app.dataset.screen !== "play" || !playing) return;
        if (document.querySelector("dialog[open]")) return; // ダイアログ中はダイアログの操作を優先
        if (e.altKey || e.metaKey) return;

        // 数字キー（上段・テンキー）。Shift を押しながらならメモ入力（e.code なら JIS 配列でも数字を判別できる）
        const m = /^(?:Digit|Numpad)([1-9])$/.exec(e.code);
        if (m && !e.ctrlKey) {
            e.preventDefault();
            input(Number(m[1]), e.shiftKey);
            return;
        }

        const moves = { ArrowUp: -9, ArrowDown: 9, ArrowLeft: -1, ArrowRight: 1 };
        if (e.key in moves) {
            e.preventDefault();
            const from = selected >= 0 ? selected : 40;
            let r = rowOf(from), c = colOf(from);
            if (e.key === "ArrowUp") r = (r + 8) % 9;       // 端では反対側へ回り込む
            if (e.key === "ArrowDown") r = (r + 1) % 9;
            if (e.key === "ArrowLeft") c = (c + 8) % 9;
            if (e.key === "ArrowRight") c = (c + 1) % 9;
            select(selected >= 0 ? r * 9 + c : 40);
            return;
        }

        switch (e.code) {
            case "Backspace":
            case "Delete":
            case "Digit0":
            case "Numpad0":
                e.preventDefault();
                eraseNotes();
                break;
            case "KeyN":
            case "KeyM":
                e.preventDefault();
                setMemo(!memoMode);
                save();
                break;
            case "KeyZ":
                e.preventDefault();
                undo();
                break;
            case "Escape":
            case "KeyP":
                e.preventDefault();
                pause();
                break;
            default:
                break;
        }
    });

    // 別のアプリ・タブに切り替えたら自動で一時停止（時計を止め、盤面を隠す）
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState !== "hidden" || !playing) return;
        pauseInBackground();
        app.classList.add("is-paused");
        if (!pauseDialog.open) pauseDialog.showModal();
        showTime();
    });
    window.addEventListener("pagehide", pauseInBackground);

    // OS のアニメーション設定が変わったとき
    osReduce.addEventListener?.("change", applyMotionSetting);

    // ===============================================================
    // 起動
    // ===============================================================
    applyMotionSetting();
    prepareBoard();
    showSelect();
})();
