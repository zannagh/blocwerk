/*
 * Sends a finished Shape-tool edit (wall-shape-drag.js) to the editor's CommitShapeEdit, once, and takes the live
 * preview away when the server has answered (it has rendered the new outline by then). If the call fails or no
 * answer comes within COMMIT_TIMEOUT_MS (the circuit dropped, or it reconnected to a server that never got the
 * edit), the preview goes too and a toast tells the user the edit was not saved: losing it silently made it look
 * saved until the page was reloaded. A late "saved" after that takes the toast back.
 */
window.bwShapeCommit = (function () {
    'use strict';

    const COMMIT_TIMEOUT_MS = 8000;
    const LOST = "Your last shape edit wasn't saved. Redo it.";

    function flatten(points) {
        const flat = [];
        points.forEach(function (p) { flat.push(p.dx, p.dy); });
        return flat;
    }

    function send(o, result, pending) {
        const edit = o.edit;
        const outline = result.control ? window.bwShapeGeometry.densify(result.points) : result.points;
        window.bwShapeLive.draw(o.ctx, outline.map(function (p) { return { x: edit.cx + p.dx, y: edit.cy + p.dy }; }), null, null);
        pending.add(edit.holdId);
        let done = false;
        const settle = function (saved) {
            if (done) {
                if (saved === true) {
                    window.bwShapeStatus.hideToast();
                }

                return;
            }

            done = true;
            clearTimeout(timer);
            pending.delete(edit.holdId);
            window.bwShapeLive.end(o.ctx);
        };
        // No answer (the circuit dropped or is reconnecting): the edit is gone with it; say so.
        const timer = setTimeout(function () {
            settle(false);
            window.bwShapeStatus.toast(LOST);
        }, COMMIT_TIMEOUT_MS);
        o.env.editor.invokeMethodAsync('CommitShapeEdit', edit.holdId, o.kind, flatten(result.points), result.control)
            .then(settle, function () {
                settle(false);
                window.bwShapeStatus.toast(LOST);
            });
    }

    return { send: send };
})();
