/*
 * edit-presence.js — tells the server a PERSON is at an open editor, so the deploy gate only waits for people.
 *
 * The edit lease (EditActivityRegistry) has two clocks: liveness (the circuit heartbeat) and presence. Presence
 * used to be "any inbound circuit traffic", and a MacBook asleep with the wall editor open reconnects its circuit
 * on every dark wake, acknowledging renders and answering JS calls as it does: the lease looked busy all night and
 * autodeploy deferred for half an hour at a time. So presence now comes only from here, and only from:
 *   - trusted pointerdown, pointermove WITH a button held (a drag), keydown and wheel events;
 *   - the page being hidden / frozen / unloaded (visibilitychange, pagehide, freeze) or shown again.
 * Never from timers, focus, resize, visibility becoming visible, or a reconnect. Showing the page again only
 * clears the hidden mark; it is not input.
 *
 * Reports go through the presence handle bwEditGuard.enter() received (EditPresenceInterop), and only while an
 * editor is open (bwEditGuard.isEditing()). Input is throttled to one report per INPUT_EVERY_MS: the server's
 * presence window is minutes long, so finer reports buy nothing. Visibility is reported on every change, when an
 * editor opens (so the server knows the reporter works) and again whenever the circuit reconnects.
 *
 * The decision core (createReporter) is pure, so node tests it (test/js/edit-presence.test.mjs).
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwEditPresence = api;
        api.install(root, api);
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const INPUT_EVERY_MS = 15000;

    // Whether an event is a person doing something, rather than the OS or the page doing it for them.
    function isUserInput(e) {
        if (!e || e.isTrusted === false) {
            return false;
        }

        switch (e.type) {
            case 'pointerdown':
            case 'keydown':
            case 'wheel':
                return true;
            case 'pointermove':
                return !!e.buttons;
            default:
                return false;
        }
    }

    // opts: { now(): ms, active(): bool, send(method, ...args) -> Promise (rejects when it did not reach the server) }
    //
    // State is committed optimistically and ROLLED BACK when the send fails, so a report lost to a reconnect (the
    // phone unlocked while the socket is still coming back) is neither remembered as sent nor throttles away the next
    // tap. resync(visible) re-sends the current state after a reconnect or when a new editor hands over its handle.
    function createReporter(opts) {
        let lastInput = -Infinity;
        let hidden = null;

        function deliver(method, args, rollback) {
            let sent;
            try {
                sent = Promise.resolve(opts.send.apply(null, [method].concat(args)));
            } catch (e) {
                sent = Promise.reject(e);
            }

            sent.catch(rollback);
        }

        function input(e) {
            if (!isUserInput(e) || !opts.active()) {
                return false;
            }

            const t = opts.now();
            if (t - lastInput < INPUT_EVERY_MS && hidden !== true) {
                return false;
            }

            const before = { lastInput: lastInput, hidden: hidden };
            lastInput = t;
            hidden = false;
            deliver('OnUserInput', [], function () {
                // Only undo what this send set; a later success must not be overwritten.
                if (lastInput === t) {
                    lastInput = before.lastInput;
                    hidden = before.hidden;
                }
            });
            return true;
        }

        function visibility(visible) {
            if (!opts.active() || hidden === !visible) {
                return false;
            }

            const before = hidden;
            hidden = !visible;
            deliver('OnVisibility', [!!visible], function () {
                if (hidden === !visible) {
                    hidden = before;
                }
            });
            return true;
        }

        function resync(visible) {
            hidden = null;
            return visibility(visible);
        }

        return { input: input, visibility: visibility, resync: resync };
    }

    function install(win, api) {
        const doc = win.document;
        const guard = function () { return win.bwEditGuard; };
        const reporter = createReporter({
            now: function () { return Date.now(); },
            active: function () { return !!(guard() && guard().isEditing() && guard().presence()); },
            send: function (method, arg) {
                const handle = guard() && guard().presence();
                if (!handle) {
                    return Promise.reject(new Error('no presence handle'));
                }

                const args = arguments.length > 1 ? [method, arg] : [method];
                return handle.invokeMethodAsync.apply(handle, args);
            }
        });
        const visibleNow = function () { return doc.visibilityState === 'visible'; };

        const onInput = function (e) { reporter.input(e); };
        ['pointerdown', 'pointermove', 'keydown', 'wheel'].forEach(function (type) {
            win.addEventListener(type, onInput, { capture: true, passive: true });
        });

        doc.addEventListener('visibilitychange', function () {
            reporter.visibility(visibleNow());
        });
        win.addEventListener('pagehide', function () { reporter.visibility(false); });
        doc.addEventListener('freeze', function () { reporter.visibility(false); });

        // An editor opened (edit-guard.js calls this with its handle) or the circuit came back: say where we are.
        api.resync = function () { reporter.resync(visibleNow()); };
        const subscribe = function () {
            if (win.blocwerkConnection && win.blocwerkConnection.subscribe) {
                win.blocwerkConnection.subscribe(function (status) {
                    if (status === 'connected') {
                        api.resync();
                    }
                });
                return true;
            }

            return false;
        };
        if (!subscribe()) {
            doc.addEventListener('DOMContentLoaded', subscribe);
        }
    }

    return { createReporter: createReporter, isUserInput: isUserInput, install: install, INPUT_EVERY_MS: INPUT_EVERY_MS };
});
