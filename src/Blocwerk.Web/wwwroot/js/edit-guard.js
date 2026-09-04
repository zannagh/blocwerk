/*
 * bwEditGuard — a browser-side ref-count of "an editor with unsaved work is open on this circuit".
 *
 * The maintenance watchdog (maintenance.js) reads isEditing() before it takes the kiosk
 * auto-reconnect reload. That reload throws away anything that lives only in circuit state — holds
 * placed in a boulder or wall editor that has not been saved yet — so it must be held back while
 * such an editor is open. The server-side edit components (the ones that take a CircuitEditActivity
 * busy lease) call enter() when they open and exit() when they close, through EditGuardInterop.
 *
 * Keyed and ref-counted so overlapping and repeated editors compose: two enter('wall-segment') plus
 * one enter('boulder-edit') need three matching exits before the guard clears. exit() never drops a
 * key below zero, so a stray exit (a torn-down circuit's dispose that still reached JS) is harmless.
 *
 * The state is per page load only: a reload starts fresh, which is exactly right — nothing is being
 * edited across a reload. It lives outside the circuit so a dropped connection cannot clear it.
 *
 * Exposes window.bwEditGuard:
 *   enter(key) -> increment the ref-count for key
 *   exit(key)  -> decrement it, never below zero
 *   isEditing() -> bool, true while any key is held
 */
(function () {
    'use strict';

    var counts = Object.create(null);

    function enter(key) {
        var k = key || 'default';
        counts[k] = (counts[k] || 0) + 1;
    }

    function exit(key) {
        var k = key || 'default';
        if (!counts[k]) {
            return;
        }

        counts[k] -= 1;
        if (counts[k] <= 0) {
            delete counts[k];
        }
    }

    function isEditing() {
        for (var k in counts) {
            if (counts[k] > 0) {
                return true;
            }
        }

        return false;
    }

    window.bwEditGuard = {
        enter: enter,
        exit: exit,
        isEditing: isEditing
    };
})();
