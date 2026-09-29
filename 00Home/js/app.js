/**
 * ホームページの画面制御。
 * 検索・並び替え・お気に入り・履歴などのロジックは C# サーバー（/api）が担当し、
 * このファイルは「API を呼んで結果を表示する」ことだけを行う。
 */
(() => {
    "use strict";

    const api = window.GameSiteApi;

    // ---------------------------------------------------------------
    // 小道具
    // ---------------------------------------------------------------
    const $ = (sel, root = document) => root.querySelector(sel);
    const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
    }[c]));

    const DAY = 86400000;
    const fmtDate = (s) => s ? new Date(s).toLocaleDateString("ja-JP", { year: "numeric", month: "short", day: "numeric" }) : "";
    function fmtAgo(s) {
        const diff = Date.now() - new Date(s).getTime();
        if (diff < 60000) return "たった今";
        if (diff < 3600000) return `${Math.floor(diff / 60000)}分前`;
        if (diff < DAY) return `${Math.floor(diff / 3600000)}時間前`;
        if (diff < 30 * DAY) return `${Math.floor(diff / DAY)}日前`;
        return fmtDate(s);
    }

    const ICON = {
        play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4.5v15l13-7.5z"/></svg>',
        heart: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10Z"/></svg>',
        close: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>',
    };

    const VIEWS = {
        all: { title: "ゲーム一覧" },
        fav: { title: "お気に入り" },
        recent: { title: "遊んだゲーム" },
    };

    // ---------------------------------------------------------------
    // 要素・状態
    // ---------------------------------------------------------------
    const el = {
        search: $("#search"),
        searchClear: $("#search-clear"),
        homeOnly: $("#home-only"),
        hero: $("#hero"),
        recent: $("#recent"),
        recentList: $("#recent-list"),
        title: $("#games-title"),
        chips: $("#tag-chips"),
        sortWrap: $("#sort-wrap"),
        sort: $("#sort"),
        clearHistory: $("#clear-history"),
        grid: $("#grid"),
        count: $("#result-count"),
        empty: $("#empty"),
        banner: $("#error-banner"),
        bannerText: $("#error-text"),
        toast: $("#toast"),
        theme: $("#theme-toggle"),
        sheet: $("#detail"),
    };

    const state = readUrl();
    const cards = new Map();   // id → 最後に受け取ったカード情報
    let tags = [];
    let listController = null;
    let lastFailed = null;

    function readUrl() {
        const p = new URLSearchParams(location.search);
        return {
            view: VIEWS[p.get("view")] ? p.get("view") : "all",
            q: p.get("q") || "",
            tag: p.get("tag") || "",
            sort: ["new", "popular", "name"].includes(p.get("sort")) ? p.get("sort") : "new",
            game: p.get("game") || "",
        };
    }

    function urlFor(s) {
        return location.pathname + api.query({
            view: s.view !== "all" ? s.view : "",
            q: s.q,
            tag: s.tag,
            sort: s.sort !== "new" ? s.sort : "",
            game: s.game,
        });
    }

    /** push=true で履歴に積む（スマホの「戻る」で前の画面に戻れるように） */
    function commitUrl(push) {
        const url = urlFor(state);
        if (url === location.pathname + location.search) return;
        try {
            push ? history.pushState({ ...state }, "", url) : history.replaceState({ ...state }, "", url);
        } catch { /* 無視 */ }
    }

    // ---------------------------------------------------------------
    // テンプレート
    // ---------------------------------------------------------------
    function coverHTML(g) {
        const img = g.thumbnailUrl ? `<img src="${esc(g.thumbnailUrl)}" alt="" loading="lazy" decoding="async" data-thumb>` : "";
        return `<div class="cover" style="--c:${esc(g.color)}"><span class="cover-glyph" aria-hidden="true">${esc(g.icon)}</span>${img}</div>`;
    }

    function badgesHTML(g, inline = false) {
        const list = [];
        if (g.isNew) list.push('<span class="badge badge-new">NEW</span>');
        if (g.status === "beta") list.push('<span class="badge badge-beta">BETA</span>');
        if (g.status === "coming-soon") list.push('<span class="badge badge-soon">準備中</span>');
        return list.length ? `<div class="badges${inline ? " badges-inline" : ""}">${list.join("")}</div>` : "";
    }

    const metaText = (g) => [g.players, g.playTime && `約${g.playTime}`].filter(Boolean).join(" · ");
    const favLabel = (g, on) => `${g.title}をお気に入り${on ? "から外す" : "に追加"}`;

    function cardHTML(g, i) {
        return `
            <article class="card${g.playable ? "" : " is-disabled"}" style="--i:${Math.min(i, 12)}">
                ${coverHTML(g)}
                ${badgesHTML(g)}
                <button type="button" class="fav-btn" data-fav="${esc(g.id)}" aria-pressed="${g.isFavorite}" aria-label="${esc(favLabel(g, g.isFavorite))}">${ICON.heart}</button>
                <div class="card-body">
                    <h3 class="card-title"><button type="button" class="card-link" data-detail="${esc(g.id)}">${esc(g.title)}</button></h3>
                    <p class="card-desc">${esc(g.summary)}</p>
                    <p class="card-meta">${esc(metaText(g))}</p>
                </div>
            </article>`;
    }

    const skeletonHTML = (n) => Array.from({ length: n }, () => `
        <div class="card skeleton" aria-hidden="true">
            <div class="cover"></div>
            <div class="card-body"><div class="sk-line"></div><div class="sk-line short"></div></div>
        </div>`).join("");

    const playHTML = (g, label = "あそぶ", cls = "btn btn-primary") => g.playable
        ? `<a class="${cls}" href="${esc(g.url)}">${ICON.play}${label}</a>`
        : `<span class="${cls}" aria-disabled="true">準備中</span>`;

    // ---------------------------------------------------------------
    // ホーム（ピックアップ・つづきから）
    // ---------------------------------------------------------------
    async function loadHome() {
        const home = await api.get("/api/home");
        $$("[data-site-title]").forEach((n) => { n.textContent = home.title; });
        document.title = home.title;
        tags = home.tags;
        renderChips();
        renderHero(home);
        renderRecent(home.recent);
        [home.featured, ...home.recent].forEach(remember);
    }

    function renderHero(home) {
        const g = home.featured;
        const s = home.stats;
        const stats = `
            <div class="hero-stats">
                <span><strong>${s.published}</strong>本 あそべます</span>
                ${s.comingSoon ? `<span><strong>${s.comingSoon}</strong>本 準備中</span>` : ""}
                ${s.lastUpdated ? `<span>更新 ${esc(fmtDate(s.lastUpdated))}</span>` : ""}
            </div>`;

        if (!g) {
            el.hero.innerHTML = `<div class="hero-copy"><p class="eyebrow">WELCOME</p>
                <h2 class="hero-title">${esc(home.title)}へようこそ</h2>
                <p class="hero-desc">ゲームは準備中です。公開をお楽しみに！</p></div>`;
            return;
        }
        el.hero.innerHTML = `
            <button type="button" class="hero-art" data-detail="${esc(g.id)}" tabindex="-1" aria-hidden="true">
                ${coverHTML(g)}${badgesHTML(g)}
            </button>
            <div class="hero-copy">
                <p class="eyebrow">${g.featured ? "PICK UP" : "NEW"}</p>
                <h2 class="hero-title">${esc(g.title)}</h2>
                <p class="hero-desc">${esc(g.summary)}</p>
                <div class="hero-actions">
                    ${playHTML(g, "いますぐ あそぶ")}
                    <button type="button" class="btn btn-ghost" data-detail="${esc(g.id)}">くわしく</button>
                </div>
                ${stats}
            </div>`;
    }

    function renderRecent(list) {
        el.recent.hidden = list.length === 0;
        el.recentList.innerHTML = list.map((g) => `
            <a class="mini" href="${esc(g.url)}">
                ${coverHTML(g)}
                <span class="mini-text">
                    <span class="mini-title">${esc(g.title)}</span>
                    <span class="mini-sub">${esc(fmtAgo(g.lastPlayedAt))} · ${g.myPlays}回</span>
                </span>
                <span class="mini-play" aria-hidden="true">${ICON.play}</span>
            </a>`).join("");
    }

    function renderChips() {
        if (state.tag && !tags.some((t) => t.name === state.tag)) state.tag = "";
        const chip = (value, label, n) => `
            <button type="button" class="chip" data-tag="${esc(value)}" aria-pressed="${state.tag === value}">
                ${esc(label)}${n != null ? ` <span class="chip-count">${n}</span>` : ""}
            </button>`;
        el.chips.innerHTML = chip("", "すべて") + tags.map((t) => chip(t.name, t.name, t.count)).join("");
        el.chips.hidden = tags.length === 0;
    }

    // ---------------------------------------------------------------
    // 一覧
    // ---------------------------------------------------------------
    async function loadList() {
        listController?.abort();
        const controller = (listController = new AbortController());

        // 通信が遅い時だけ読み込み表示を出す（速い時にチラつかせない）
        const skeletonTimer = setTimeout(() => {
            el.grid.innerHTML = skeletonHTML(4);
            el.grid.setAttribute("aria-busy", "true");
            el.empty.hidden = true;
        }, 150);

        try {
            const data = await api.get("/api/games" + api.query({
                view: state.view, q: state.q, tag: state.tag, sort: state.sort,
            }), { signal: controller.signal });
            data.items.forEach(remember);
            renderList(data);
            hideError();
        } catch (err) {
            if (err.name !== "AbortError") showError(err, loadList);
        } finally {
            clearTimeout(skeletonTimer);
            if (listController === controller) el.grid.setAttribute("aria-busy", "false");
        }
    }

    function renderList(data) {
        const items = data.items;
        el.grid.innerHTML = items.map(cardHTML).join("");
        const filtered = state.q || state.tag;
        el.count.textContent = state.view === "all" && !filtered ? `${data.total}本` : `${items.length}本`;

        el.empty.hidden = items.length > 0;
        if (!items.length) renderEmpty(filtered, data.total);
    }

    function renderEmpty(filtered, total) {
        const set = (icon, title, text, action) => {
            $("#empty-icon").textContent = icon;
            $("#empty-title").textContent = title;
            $("#empty-text").textContent = text;
            const btn = $("#empty-action");
            btn.hidden = !action;
            if (action) { btn.textContent = action.label; btn.onclick = action.run; }
        };
        const reset = { label: "条件をリセット", run: () => { state.q = ""; state.tag = ""; el.search.value = ""; update(); } };
        const toAll = { label: "ゲームを見る", run: () => setView("all", true) };

        if (filtered) set("🔍", "見つかりませんでした", "ことばやタグを変えてみてください。", reset);
        else if (state.view === "fav") set("♡", "お気に入りはまだありません", "カードの ♡ を押すと、ここに集まります。", toAll);
        else if (state.view === "recent") set("🕹️", "まだ遊んだゲームはありません", "遊んだゲームがここに並びます。", toAll);
        else if (!total) set("🛠️", "ゲームは準備中です", "公開をお楽しみに！", null);
        else set("🔍", "見つかりませんでした", "", null);
    }

    function remember(g) { if (g) cards.set(g.id, g); }

    // ---------------------------------------------------------------
    // 画面の切り替え（ホーム / お気に入り / 履歴）
    // ---------------------------------------------------------------
    function applyView() {
        const v = state.view;
        $$(".tab[data-view]").forEach((t) => {
            if (t.dataset.view === v) t.setAttribute("aria-current", "page");
            else t.removeAttribute("aria-current");
        });
        el.homeOnly.hidden = v !== "all";
        el.title.textContent = VIEWS[v].title;
        el.sortWrap.hidden = v === "recent";
        el.clearHistory.hidden = v !== "recent";
        el.sort.value = state.sort;
        el.search.value = state.q;
        el.searchClear.hidden = !state.q;
        $$(".chip[data-tag]", el.chips).forEach((c) => c.setAttribute("aria-pressed", String(c.dataset.tag === state.tag)));
    }

    function setView(view, push) {
        if (state.view === view) {
            window.scrollTo({ top: 0, behavior: "smooth" });
            return;
        }
        state.view = view;
        commitUrl(push);
        applyView();
        window.scrollTo({ top: 0 });
        loadList();
    }

    function update(push = false) {
        commitUrl(push);
        applyView();
        loadList();
    }

    // ---------------------------------------------------------------
    // 詳細シート
    // ---------------------------------------------------------------
    let sheetToken = 0;
    let sheetPushed = false; // シートを開いた時に履歴を積んだか
    let lastFocus = null;

    function openDetail(id, { push = true } = {}) {
        const known = cards.get(id);
        const token = ++sheetToken;
        const wasOpen = el.sheet.open;
        el.sheet.dataset.id = id;
        el.sheet.innerHTML = known ? sheetHTML(known, null) : `<div class="sheet-body"><p>読み込み中…</p></div>`;

        if (!wasOpen) {
            lastFocus = document.activeElement;
            sheetPushed = push;
            el.sheet.showModal();
        }
        el.sheet.scrollTop = 0;
        if (state.game !== id) {
            state.game = id;
            commitUrl(push && !wasOpen);
        }
        $("[data-close]", el.sheet)?.focus({ preventScroll: true });

        api.get(`/api/games/${encodeURIComponent(id)}`).then((detail) => {
            if (token !== sheetToken || !el.sheet.open) return;
            remember(detail.game);
            el.sheet.innerHTML = sheetHTML(detail.game, detail);
        }).catch((err) => {
            if (token !== sheetToken) return;
            if (!known) el.sheet.innerHTML = `<div class="sheet-body"><p>${esc(err.message)}</p>
                <button type="button" class="btn btn-ghost" data-close>とじる</button></div>`;
        });
    }

    function sheetHTML(g, d) {
        const meta = [
            ["ジャンル", g.genre], ["人数", g.players], ["プレイ時間", g.playTime && `約${g.playTime}`],
            ["操作", d?.controls?.join(" / ")], ["公開日", d?.added && fmtDate(d.added)],
            ["みんなのプレイ", g.totalPlays ? `${g.totalPlays.toLocaleString()}回` : null],
        ].filter(([, v]) => v);

        const board = d?.leaderboard;
        const modeLabel = board && d.modes.find((m) => m.id === board.mode)?.label;
        const ranking = !board ? "" : `
            <div>
                <h3>ランキング${modeLabel ? `（${esc(modeLabel)}）` : ""}</h3>
                ${board.entries.length ? `<ol class="ranking">${board.entries.map((e) => `
                    <li class="${e.isMe ? "is-me" : ""}"><span class="rank">${e.rank}</span>
                    <span class="name">${esc(e.name)}${e.isMe ? "（あなた）" : ""}</span>
                    <span class="score">${e.score.toLocaleString()}</span></li>`).join("")}</ol>`
                    : `<p class="ranking-empty">まだ記録がありません。一番乗りを目指そう！</p>`}
            </div>`;

        return `
            <div class="sheet-grip" aria-hidden="true"></div>
            <button type="button" class="icon-btn sheet-close" data-close aria-label="とじる">${ICON.close}</button>
            ${coverHTML(g)}
            <div class="sheet-body">
                <div>
                    ${badgesHTML(g, true)}
                    <h2 class="sheet-title" id="detail-title">${esc(g.title)}</h2>
                </div>
                ${(d?.description || g.summary) ? `<p class="sheet-desc">${esc(d?.description || g.summary)}</p>` : ""}
                ${meta.length ? `<dl class="meta-list">${meta.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join("")}</dl>` : ""}
                ${d?.howToPlay?.length ? `<div><h3>あそびかた</h3><ul class="howto">${d.howToPlay.map((s) => `<li>${esc(s)}</li>`).join("")}</ul></div>` : ""}
                ${ranking}
                ${g.myPlays ? `<p class="my-stats">あなたは ${g.myPlays}回 遊びました（${esc(fmtAgo(g.lastPlayedAt))}）</p>` : ""}
                ${g.tags.length ? `<div><h3>タグ</h3><div class="tag-list">${g.tags.map((t) => `<button type="button" class="chip" data-tag="${esc(t)}">#${esc(t)}</button>`).join("")}</div></div>` : ""}
                <div class="sheet-actions">
                    ${playHTML(g, "あそぶ", "btn btn-primary btn-block")}
                    <button type="button" class="btn btn-ghost fav-toggle" data-fav="${esc(g.id)}" aria-pressed="${g.isFavorite}" aria-label="${esc(favLabel(g, g.isFavorite))}">${ICON.heart}</button>
                </div>
            </div>`;
    }

    /** 閉じる：自分で積んだ履歴なら「戻る」で閉じる（端末の戻るボタンと挙動を揃える） */
    function closeDetail() {
        if (!el.sheet.open) return;
        if (sheetPushed) history.back(); // popstate で finishClose される
        else finishClose();
    }

    function finishClose() {
        sheetToken++;
        sheetPushed = false;
        if (el.sheet.open) el.sheet.close();
        if (state.game) {
            state.game = "";
            commitUrl(false);
        }
        if (lastFocus && document.contains(lastFocus)) lastFocus.focus({ preventScroll: true });
    }

    el.sheet.addEventListener("cancel", (e) => { e.preventDefault(); closeDetail(); }); // Esc キー
    el.sheet.addEventListener("click", (e) => { if (e.target === el.sheet) closeDetail(); }); // 背景タップ

    // 下にスワイプして閉じる
    (() => {
        let startY = null, dy = 0;
        el.sheet.addEventListener("touchstart", (e) => {
            startY = el.sheet.scrollTop <= 0 ? e.touches[0].clientY : null;
            dy = 0;
        }, { passive: true });
        el.sheet.addEventListener("touchmove", (e) => {
            if (startY == null) return;
            dy = Math.max(0, e.touches[0].clientY - startY);
            el.sheet.style.transform = dy ? `translateY(${dy}px)` : "";
        }, { passive: true });
        el.sheet.addEventListener("touchend", () => {
            if (startY == null) return;
            el.sheet.style.transform = "";
            if (dy > 110) closeDetail();
            startY = null;
        });
    })();

    // ---------------------------------------------------------------
    // お気に入り（先に画面を変えて、失敗したら元に戻す）
    // ---------------------------------------------------------------
    async function toggleFavorite(id) {
        const g = cards.get(id);
        const on = !(g?.isFavorite);
        paintFavorite(id, on);
        try {
            const path = `/api/me/favorites/${encodeURIComponent(id)}`;
            on ? await api.put(path) : await api.del(path);
            toast(on ? "お気に入りに追加しました" : "お気に入りから外しました");
            if (state.view === "fav") loadList();
        } catch (err) {
            paintFavorite(id, !on);
            toast(err.message);
        }
    }

    function paintFavorite(id, on) {
        const g = cards.get(id);
        if (g) g.isFavorite = on;
        $$(`[data-fav="${CSS.escape(id)}"]`).forEach((b) => {
            b.setAttribute("aria-pressed", String(on));
            if (g) b.setAttribute("aria-label", favLabel(g, on));
            b.classList.remove("pop");
            void b.offsetWidth;
            if (on) b.classList.add("pop");
        });
    }

    // ---------------------------------------------------------------
    // エラー表示・トースト
    // ---------------------------------------------------------------
    function showError(err, retry) {
        lastFailed = retry;
        el.bannerText.textContent = err.message;
        el.banner.hidden = false;
    }
    function hideError() { el.banner.hidden = true; }

    let toastTimer;
    function toast(msg) {
        el.toast.textContent = msg;
        el.toast.classList.add("show");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => el.toast.classList.remove("show"), 2200);
    }

    // ---------------------------------------------------------------
    // テーマ
    // ---------------------------------------------------------------
    const mql = window.matchMedia("(prefers-color-scheme: dark)");
    const currentTheme = () => document.documentElement.dataset.theme || (mql.matches ? "dark" : "light");
    function paintTheme() {
        const dark = currentTheme() === "dark";
        el.theme.dataset.mode = dark ? "dark" : "light";
        el.theme.setAttribute("aria-label", dark ? "明るい画面にする" : "暗い画面にする");
    }
    el.theme.addEventListener("click", () => {
        const next = currentTheme() === "dark" ? "light" : "dark";
        document.documentElement.dataset.theme = next;
        try { localStorage.setItem("gs:theme", JSON.stringify(next)); } catch { /* 無視 */ }
        paintTheme();
    });
    mql.addEventListener?.("change", paintTheme);

    // ---------------------------------------------------------------
    // イベント
    // ---------------------------------------------------------------
    document.addEventListener("click", (e) => {
        const t = e.target.closest("[data-fav], [data-detail], [data-tag], [data-close], [data-view], [data-view-link]");
        if (!t) return;

        if (t.matches("[data-fav]")) {
            e.preventDefault();
            toggleFavorite(t.dataset.fav);
        } else if (t.matches("[data-detail]")) {
            openDetail(t.dataset.detail);
        } else if (t.matches("[data-close]")) {
            closeDetail();
        } else if (t.matches("[data-tag]")) {
            const inSheet = el.sheet.contains(t);
            state.tag = !inSheet && state.tag === t.dataset.tag ? "" : t.dataset.tag;
            if (inSheet) {
                // シートを閉じてから一覧を絞り込む（シートの履歴エントリを置き換える）
                const replace = sheetPushed;
                sheetPushed = false;
                finishClose();
                state.view = "all";
                update(!replace);
                $("#games").scrollIntoView({ behavior: "smooth" });
            } else {
                update();
            }
        } else if (t.matches("[data-view]")) {
            setView(t.dataset.view, true);
        } else if (t.matches("[data-view-link]")) {
            e.preventDefault();
            setView(t.dataset.viewLink, true);
        }
    });

    // 検索（入力が止まってからサーバーに問い合わせる）
    let searchTimer;
    el.search.addEventListener("input", () => {
        el.searchClear.hidden = !el.search.value;
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            state.q = el.search.value.trim();
            update();
        }, 250);
    });
    $("#search-form").addEventListener("submit", (e) => {
        e.preventDefault();
        clearTimeout(searchTimer);
        state.q = el.search.value.trim();
        el.search.blur(); // スマホのキーボードを閉じる
        update();
        $("#games").scrollIntoView({ behavior: "smooth" });
    });
    el.searchClear.addEventListener("click", () => {
        el.search.value = "";
        state.q = "";
        update();
        el.search.focus();
    });

    el.sort.addEventListener("change", () => {
        state.sort = el.sort.value;
        update();
    });

    $("#random-btn").addEventListener("click", async () => {
        try {
            const g = await api.get("/api/games/random" + api.query({
                view: state.view, q: state.q, tag: state.tag, exclude: state.game,
            }));
            remember(g);
            openDetail(g.id);
        } catch (err) {
            toast(err.message);
        }
    });

    el.clearHistory.addEventListener("click", async () => {
        if (!confirm("遊んだ記録を消しますか？（お気に入りとランキングは消えません）")) return;
        try {
            await api.del("/api/me/history");
            toast("履歴を消しました");
            loadList();
            loadHome().catch(() => {});
        } catch (err) {
            toast(err.message);
        }
    });

    $("#retry-btn").addEventListener("click", () => {
        hideError();
        loadHome().catch((err) => showError(err, loadHome));
        (lastFailed || loadList)();
    });

    // サムネイルが読めない時は自動生成カバーを表示
    document.addEventListener("error", (e) => {
        if (e.target.matches?.("img[data-thumb]")) e.target.remove();
    }, true);

    // 「戻る」「進む」
    window.addEventListener("popstate", () => {
        const next = readUrl();
        const listChanged = ["view", "q", "tag", "sort"].some((k) => next[k] !== state[k]);
        Object.assign(state, next);
        if (!state.game && el.sheet.open) finishClose();
        else if (state.game && state.game !== el.sheet.dataset.id) openDetail(state.game, { push: false });
        if (listChanged) {
            applyView();
            loadList();
        }
    });

    // ゲームから戻ってきた時（ページがキャッシュから復元された時）に最新化
    window.addEventListener("pageshow", (e) => {
        if (!e.persisted) return;
        loadHome().catch(() => {});
        loadList();
    });

    // PC 用：/ キーで検索
    document.addEventListener("keydown", (e) => {
        const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName);
        if (e.key === "/" && !typing && !el.sheet.open && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
            el.search.focus();
        }
    });

    // ---------------------------------------------------------------
    // 起動
    // ---------------------------------------------------------------
    $("#year").textContent = new Date().getFullYear();
    paintTheme();
    applyView();
    commitUrl(false);
    loadHome().catch((err) => showError(err, loadHome));
    loadList();
    if (state.game) openDetail(state.game, { push: false });
})();
