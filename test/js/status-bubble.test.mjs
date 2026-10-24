// The status bubbles: bwStatus (status-bubble.js), what status-sources.js tells it from the offline queue and the
// circuit, and how the connection pill (offline-status.js) draws them. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { statusPage } from './fixtures/status-dom.mjs';

const bubbleOnly = opts => statusPage(Object.assign({ modules: ['status-bubble.js'] }, opts));
const shownKind = page => (page.win.bwStatus.current() || {}).kind || null;

test('the wording of each kind', () => {
    const { bwStatus } = bubbleOnly().win;
    assert.deepEqual(Object.keys(bwStatus.KINDS).sort(), ['conflict', 'offline', 'offline-ready', 'online', 'uploaded', 'uploading']);
    assert.equal(bwStatus.label('offline-ready'), 'Offline mode available');
    assert.equal(bwStatus.label('offline'), 'Switched to offline mode');
    assert.equal(bwStatus.label('online'), 'Switched to online mode');
    assert.equal(bwStatus.label('uploading', { count: 3 }), 'Uploading 3 changes');
    assert.equal(bwStatus.label('uploading'), 'Uploading changes');
    assert.equal(bwStatus.label('uploaded', { count: 1 }), '1 change uploaded');
    assert.equal(bwStatus.label('uploaded'), 'Changes uploaded');
    assert.equal(bwStatus.label('conflict', { count: 2 }), '2 changes could not be uploaded');
    assert.equal(bwStatus.label('nonsense'), '');
});

test('the newest bubble wins; only the same kind and count is dropped, and only while it is on screen', () => {
    const page = bubbleOnly();
    const { bwStatus } = page.win;
    assert.equal(bwStatus.show('offline'), true);
    assert.equal(bwStatus.show('offline'), false, 'already up');
    assert.equal(bwStatus.show('online'), true);
    assert.equal(bwStatus.show('offline'), true, 'offline, online, offline: ends on the true state');
    assert.equal(shownKind(page), 'offline');
    page.advance(4000);
    assert.equal(shownKind(page), null, 'gone after 4 s');
    assert.equal(bwStatus.show('offline'), true, 'and told again for a new outage at once');
    assert.equal(bwStatus.show('nonsense'), false);
});

test('"Offline mode available" waits for a quiet pill; a conflict gives way only to the connection or a newer conflict', () => {
    const page = bubbleOnly();
    const { bwStatus } = page.win;
    bwStatus.show('uploading', { count: 2 });
    assert.equal(bwStatus.show('offline-ready'), false, 'never over another bubble');
    page.advance(4000);
    assert.equal(bwStatus.show('offline-ready'), true);
    assert.equal(bwStatus.show('online'), true, 'anything replaces it');

    assert.equal(bwStatus.show('conflict', { count: 1 }), true);
    assert.equal(bwStatus.show('uploaded', { count: 1 }), false, 'an "uploaded" of the same flush does not paint over it');
    assert.equal(bwStatus.show('conflict', { count: 2 }), true, 'a further failure updates it');
    assert.equal(bwStatus.show('offline'), true, 'going offline always shows');
    assert.equal(bwStatus.show('conflict', { count: 2 }), true, 'a conflict after Dismiss shows again, whatever came before');
    page.advance(6000);
    assert.equal(shownKind(page), null, 'a conflict bubble lasts 6 s; the pill keeps its badge');
});

test('spoken politely, a conflict assertively; offline-ready is never shown on a kiosk', () => {
    const page = bubbleOnly();
    const { bwStatus } = page.win;
    bwStatus.show('offline-ready', { count: 2 });
    page.advance(100);
    const polite = page.live('bw-status-polite');
    assert.equal(polite.getAttribute('role'), 'status');
    assert.equal(polite.getAttribute('aria-live'), 'polite');
    assert.equal(polite.textContent, 'Offline mode available');

    bwStatus.show('conflict', { count: 1 });
    page.advance(100);
    const alert = page.live('bw-status-alert');
    assert.equal(alert.getAttribute('role'), 'alert');
    assert.equal(alert.getAttribute('aria-live'), 'assertive');
    assert.equal(alert.textContent, '1 change could not be uploaded');

    // A bubble replaced before it is spoken is not spoken (politely); a conflict always is.
    page.advance(10000);
    bwStatus.show('offline');
    bwStatus.show('online');
    page.advance(100);
    assert.equal(polite.textContent, 'Switched to online mode');

    const kiosk = bubbleOnly({ kiosk: true });
    assert.equal(kiosk.win.bwStatus.show('offline-ready', { count: 2 }), false);
    assert.equal(kiosk.win.bwStatus.show('offline'), true, 'a kiosk already shows its connection in the pill');
});

test('without the pill (the offline client), bwStatus draws a pill of its own, tappable with onOpen', () => {
    const page = bubbleOnly();
    let opened = 0;
    page.win.bwStatus.show('conflict', { count: 1, onOpen: () => { opened++; } });
    const pill = page.pill();
    assert.equal(pill.tagName, 'BUTTON');
    assert.equal(pill.getAttribute('data-bw-status'), 'conflict');
    assert.ok(pill.classList.contains('bw-conn-error'));
    pill.click();
    assert.equal(opened, 1);
    page.win.bwStatus.hide();
    assert.equal(page.pill(), null);
});

test('the pill draws the bubble, then falls back to the lasting state, updating in place', () => {
    const page = statusPage();
    page.queue({ online: false });
    page.advance(0);
    const pill = page.pill();
    assert.equal(pill.textContent, 'Switched to offline mode');
    assert.equal(pill.getAttribute('data-bw-status'), 'offline');
    page.advance(4000);
    assert.equal(page.pill(), pill, 'the same element');
    assert.equal(pill.textContent, 'Offline');
    assert.equal(pill.getAttribute('data-bw-status'), null);

    page.queue({ online: true });
    assert.equal(page.pill().textContent, 'Switched to online mode');
    assert.equal(page.pill().tagName, 'DIV', 'nothing lasting behind it: nothing to open');
    page.advance(3000);
    assert.equal(page.pill(), null);
});

test('urgent lasting states always show; a pending conflict is a badge and never hides Offline or the bubbles', () => {
    const page = statusPage();
    page.queue({ rejected: [{ kind: 'attempt', message: 'Boulder was deleted', at: 1 }] });
    assert.equal(page.pill().textContent, '1 change could not be uploaded');
    page.advance(6000);
    assert.equal(page.pill().textContent, '1 change could not be uploaded', 'nothing else to say: the conflict is the label');
    assert.equal(page.pill().tagName, 'BUTTON');

    page.queue({ online: false, count: 2 });
    page.advance(0);
    assert.equal(shownKind(page), 'offline', 'offline is told over a pending conflict');
    assert.match(page.pill().textContent, /^Switched to offline mode1$/, 'with the badge');
    page.advance(4000);
    assert.equal(page.pill().textContent, 'Offline · 21');
    assert.equal(page.pill().querySelector('.bw-conn-badge').getAttribute('aria-label'), '1 change could not be uploaded');

    page.queue({ paused: true, online: true });
    assert.equal(page.pill().textContent, 'Sign in to sync1');
    page.win.bwStatus.show('uploaded', { count: 1 });
    assert.equal(page.pill().textContent, 'Sign in to sync1', 'urgent: no bubble over it');
});

test('the panel lists the conflict, keeps focus on its buttons while bubbles come and go, and Dismiss clears it', () => {
    const page = statusPage();
    page.queue({ online: false, count: 1, rejected: [{ kind: 'attempt', message: 'Boulder was deleted', at: 1 }] });
    page.advance(5000);
    page.pill().click();
    const panel = page.panel();
    assert.match(panel.textContent, /attempt: Boulder was deleted/);
    const retry = panel.querySelector('[data-bw-key="retry"]');
    retry.focus();

    page.queue({ online: true });
    page.advance(3000);
    page.queue({ count: 1 });
    assert.equal(page.panel(), panel, 'the panel stays while only the label changes');
    assert.equal(page.document.activeElement, retry);

    page.queue({ rejected: [{ kind: 'attempt', message: 'Boulder was deleted', at: 1 }, { kind: 'comment', message: 'Gone', at: 2 }] });
    assert.notEqual(page.panel(), panel, 'what it lists changed: built again');
    assert.equal(page.document.activeElement.getAttribute('data-bw-key'), 'retry', 'focus back on the same button');

    page.panel().querySelector('[data-bw-key="dismiss"]').click();
    assert.equal(page.pill().querySelector('.bw-conn-badge'), null);
    assert.equal(page.pill().textContent, 'Uploading · 1');
});

test('offline: told at once when the device has no network, after a 4 s grace for a circuit that drops', () => {
    const page = statusPage();
    page.connection('reconnecting');
    page.advance(3000);
    page.connection('connected');
    page.advance(10000);
    assert.equal(shownKind(page), null, 'a blip that healed says nothing, and so no "online" either');

    page.connection('reconnecting');
    page.advance(4000);
    assert.equal(shownKind(page), 'offline');
    page.connection('failed');
    page.connection('connected');
    assert.equal(shownKind(page), 'online');

    page.advance(60000);
    page.connection('reconnecting');
    page.advance(4000);
    assert.equal(shownKind(page), 'offline');
    page.connection('rejected');
    page.advance(10000);
    assert.equal(shownKind(page), null, 'Session ended is not "online"');
    assert.equal(page.pill().textContent, 'Session ended');
    assert.notEqual(page.live('bw-status-polite').textContent, 'Switched to online mode');

    page.connection('connected');
    page.advance(60000);
    page.win.bwMaintenance = { active: () => true, state: () => ({ phase: 'draining', message: null }) };
    page.connection('reconnecting');
    page.advance(10000);
    assert.equal(shownKind(page), null, 'a deploy: the pill says "Blocwerk is updating…" instead');
});

test('uploading and uploaded: only for a backlog, never for an action sent straight away', () => {
    const page = statusPage();
    // The page's first flush, with nothing queued.
    page.queue({ flushing: true });
    page.queue({ flushing: false });
    // Online: an action queued and sent at once.
    page.queue({ count: 1 });
    page.queue({ flushing: true });
    page.queue({ flushing: false, count: 0 });
    page.flushed({ sent: 1, dropped: 0, held: 0, remaining: 0 });
    assert.equal(shownKind(page), null);

    // Offline: two actions saved, then the network comes back and they are sent.
    page.queue({ online: false });
    page.advance(5000);
    page.queue({ count: 2 });
    page.queue({ online: true });
    assert.equal(shownKind(page), 'online');
    page.queue({ flushing: true });
    assert.equal(page.win.bwStatus.current().label, 'Uploading 2 changes');
    page.queue({ flushing: false, count: 0 });
    page.flushed({ sent: 2, dropped: 0, held: 0, remaining: 0 });
    assert.equal(page.win.bwStatus.current().label, '2 changes uploaded');
    page.advance(3000);
    assert.equal(shownKind(page), null);
});

test('a backlog found when the page loads is uploaded with a bubble, and one sent while the circuit reconnects too', () => {
    const page = statusPage();
    page.queue({ count: 3 });
    page.queue({ flushing: true });
    assert.equal(page.win.bwStatus.current().label, 'Uploading 3 changes');
    page.queue({ flushing: false, count: 0 });
    page.flushed({ sent: 3, dropped: 0, held: 0, remaining: 0 });
    assert.equal(page.win.bwStatus.current().label, '3 changes uploaded');
    page.advance(60000);

    // Circuit down long enough to be offline, an action saved, the queue's tick sends it before the circuit is back.
    page.connection('reconnecting');
    page.advance(5000);
    page.queue({ count: 1 });
    page.queue({ flushing: true });
    assert.equal(shownKind(page), 'offline', 'not "uploading" while still offline');
    page.queue({ flushing: false, count: 0 });
    page.flushed({ sent: 1, dropped: 0, held: 0, remaining: 0 });
    assert.equal(page.win.bwStatus.current().label, '1 change uploaded');
});

test('a backlog with a refused action: the conflict is told and stays; "uploaded" counts only what was sent', () => {
    const page = statusPage();
    page.queue({ flushing: true });
    page.queue({ flushing: false });
    page.queue({ online: false });
    page.queue({ count: 3 });
    page.queue({ online: true });
    page.queue({ flushing: true });
    assert.equal(page.win.bwStatus.current().label, 'Uploading 3 changes');
    page.queue({ flushing: false, count: 0, rejected: [{ kind: 'attempt', message: 'Boulder not found', at: 1 }] });
    page.flushed({ sent: 2, dropped: 1, held: 0, remaining: 0 });
    assert.equal(page.win.bwStatus.current().label, '1 change could not be uploaded', 'the conflict outranks "uploaded"');
    assert.equal(page.pill().textContent, '1 change could not be uploaded');

    page.win.blocwerkOfflineQueue.dismissRejected();
    page.advance(60000);
    page.queue({ online: false });
    page.queue({ count: 1 });
    page.queue({ online: true });
    page.queue({ flushing: true });
    page.queue({ flushing: false, count: 0 });
    page.flushed({ sent: 1, dropped: 0, held: 0, remaining: 0 });
    assert.equal(page.win.bwStatus.current().label, '1 change uploaded');
});

test('only what this person can send now is a backlog: not held or parked entries, and nothing before it is due', () => {
    const page = statusPage();
    // A kiosk page loads with another member's 3 held entries and one a 400 parked.
    page.queue({ count: 4, held: 3, deferred: 1 });
    page.queue({ flushing: true });
    page.queue({ flushing: false });
    assert.equal(shownKind(page), null, 'no false "Uploading"');

    // This member's two taps while offline, still backing off when the network is back.
    page.queue({ online: false });
    page.queue({ count: 6, dueAt: Date.now() + 60000 });
    page.queue({ online: true });
    page.advance(3000);
    page.queue({ flushing: true });
    page.queue({ flushing: false });
    assert.equal(shownKind(page), null, 'nothing due: nothing said yet');
    page.queue({ dueAt: 0 });
    page.queue({ flushing: true });
    assert.equal(page.win.bwStatus.current().label, 'Uploading 2 changes');
    page.queue({ flushing: false, count: 4 });
    page.flushed({ sent: 2, dropped: 0, held: 3, remaining: 4 });
    assert.equal(page.win.bwStatus.current().label, '2 changes uploaded', 'told though the held entries remain');
});

test('a page that loads with kept conflicts tells them once', () => {
    const page = statusPage();
    page.queue({ rejected: [{ kind: 'attempt', message: 'x', at: 1 }] });
    assert.equal(shownKind(page), 'conflict');
    page.advance(6000);
    page.queue({ count: 0 });
    assert.equal(shownKind(page), null);
    assert.equal(page.pill().textContent, '1 change could not be uploaded');
});
