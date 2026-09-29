/**
 * C# サーバー（/api）を呼び出す共通クライアント。
 * ホームページと各ゲームの両方から読み込んで使う。
 *
 *   <script src="/00Home/js/api.js"></script>
 *   const home = await GameSiteApi.get("/api/home");
 */
(() => {
    "use strict";

    const PLAYER_KEY = "gs:player";
    let memoryId = null; // localStorage が使えない環境用

    /** ログイン不要のプレイヤー ID（初回にランダム生成してブラウザに保存） */
    function playerId() {
        try {
            let id = localStorage.getItem(PLAYER_KEY);
            if (!isGuid(id)) {
                id = newGuid();
                localStorage.setItem(PLAYER_KEY, id);
            }
            return id;
        } catch {
            return (memoryId ??= newGuid());
        }
    }

    const isGuid = (s) => typeof s === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);

    function newGuid() {
        if (crypto.randomUUID) return crypto.randomUUID();
        const b = crypto.getRandomValues(new Uint8Array(16));
        b[6] = (b[6] & 0x0f) | 0x40;
        b[8] = (b[8] & 0x3f) | 0x80;
        const h = [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
        return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
    }

    class ApiError extends Error {
        constructor(message, status, data) {
            super(message);
            this.status = status;
            this.data = data;
        }
    }

    const STATUS_MESSAGES = {
        0: "通信できませんでした。電波の良い場所でもう一度お試しください。",
        404: "見つかりませんでした。",
        410: "時間切れになりました。もう一度はじめてください。",
        429: "操作が多すぎます。少し待ってからお試しください。",
        500: "サーバーでエラーが起きました。時間をおいてお試しください。",
    };

    async function request(method, path, body, { signal } = {}) {
        const headers = { "X-Player-Id": playerId(), Accept: "application/json" };
        if (body !== undefined) headers["Content-Type"] = "application/json";

        let res;
        try {
            res = await fetch(path, {
                method,
                headers,
                body: body === undefined ? undefined : JSON.stringify(body),
                signal,
                cache: "no-store",
            });
        } catch (err) {
            if (err.name === "AbortError") throw err;
            throw new ApiError(STATUS_MESSAGES[0], 0);
        }

        if (res.status === 204) return null;
        const data = await res.json().catch(() => null);
        if (!res.ok) {
            const message = data?.detail || STATUS_MESSAGES[res.status] || STATUS_MESSAGES[500];
            throw new ApiError(message, res.status, data);
        }
        return data;
    }

    /** クエリ文字列を組み立てる（空の値は省く） */
    function query(params) {
        const p = new URLSearchParams();
        for (const [k, v] of Object.entries(params)) {
            if (v !== undefined && v !== null && v !== "") p.set(k, v);
        }
        const s = p.toString();
        return s ? `?${s}` : "";
    }

    window.GameSiteApi = {
        playerId,
        query,
        ApiError,
        get: (path, opts) => request("GET", path, undefined, opts),
        post: (path, body, opts) => request("POST", path, body ?? {}, opts),
        put: (path, body, opts) => request("PUT", path, body ?? {}, opts),
        del: (path, opts) => request("DELETE", path, undefined, opts),
    };
})();
