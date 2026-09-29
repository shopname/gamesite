/**
 * ゲームのひな形（ブラウザ側）。
 * JS は「表示」と「操作をサーバーに送る」だけ。答えや判定は C#（TemplateGame.cs）が持つ。
 */
(() => {
    "use strict";

    GameKit.init({ title: "数あて" });
    const $ = (id) => document.getElementById(id);
    let session = null;

    async function newGame() {
        try {
            session = await GameKit.start();          // → C# の CreateSession
            $("message").textContent = "1〜100 のどれかな？";
            $("tries").textContent = "0 回目";
            $("number").value = "";
        } catch (err) {
            GameKit.handleError(err);
        }
    }

    $("form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const number = Number($("number").value);
        try {
            const { state, events } = await session.act({ number }); // → C# の Handle
            $("tries").textContent = `${state.tries} 回目`;
            for (const ev of events) {
                if (ev.type === "higher") $("message").textContent = `${number} より大きいよ`;
                if (ev.type === "lower") $("message").textContent = `${number} より小さいよ`;
                if (ev.type === "correct") {
                    $("message").textContent = "正解！";
                    GameKit.showResult({ session, details: [["回数", `${ev.tries}回`]], onRetry: newGame });
                }
            }
            $("number").select();
        } catch (err) {
            GameKit.handleError(err, newGame);
        }
    });

    newGame();
})();
