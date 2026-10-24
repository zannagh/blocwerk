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

test('a bubble is shown once: a repeat while up or within 30 s is dropped, and it times out', () => {
    const page = bubbleOnly();
    const { bwStatus } = page.win;
    assert.equal(bwStatus.show('offline'), true);
    assert.equal(bwStatus.show('offline'), false, 'already up');
    page.advance(4000);
    assert.equal(shownKind(page), null, 'gone after 4 s');
    assert.equal(bwStatus.show('offline'), false, 'within 30 s of the last one');
    page.advance(30000);
    assert.equal(bwStatus.show('offline'), true, 'a new episode later is told again');
    assert.equal(bwStatus.show('nonsense'), false);
});

test('a less important bubble does not replace one that is up; a conflict stays until hidden', () => {
    const page = bubbleOnly();
    const { bwStatus } = page.win;
    bwStatus.show('uploading', { count: 2 });
    assert.equal(bwStatus.show('online'), false, 'online ranks below uploading');
    assert.equal(bwStatus.show('offline-ready', { count: 1 }), false);
    assert.equal(bwStatus.show('offline'), true, 'offline ranks with uploading and replaces it');

    assert.equal(bwStatus.show('conflict', { count: 1 }), true);
    page.advance(600000);
    assert.equal(shownKind(page), 'conflict', 'no timeout');
    assert.equal(bwStatus.show('uploaded', { count: 1 }), false);
    assert.equal(bwStatus.show('conflict', { count: 2 }), true, 'a further failure updates it');
    assert.equal(bwStatus.current().label, '2 changes could not be uploaded');
    bwStatus.hide('online');
    assert.equal(shownKind(page), 'conflict', 'hide(kind) only takes down that kind');
    bwStatus.hide('conflict');
    assert.equal(shownKind(page), null);
    assert.equal(bwStatus.show('conflict', { count: 2 }), true, 'dismissed: the next failure is told at once');
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

test('the pill draws the bubble, then falls back to the lasting state', () => {
    const page = statusPage();
    page.queue({ online: false });
    page.advance(0);
    assert.equal(page.pill().textContent, 'Switched to offline mode');
    assert.equal(page.pill().getAttribute('data-bw-status'), 'offline');
    page.advance(4000);
    assert.equal(page.pill().textContent, 'Offline');
    assert.equal(page.pill().getAttribute('data-bw-status'), null);

    page.queue({ online: true });
    assert.equal(page.pill().textContent, 'Switched to online mode');
    assert.equal(page.pill().tagName, 'DIV', 'nothing lasting behind it: nothing to open');
    page.advance(3000);
    assert.equal(page.pill(), null);
});

test('an urgent lasting state (a session to sign in to) outranks a bubble; a conflict does not', () => {
    const page = statusPage();
    page.queue({ online: false });
    page.advance(0);
    page.queue({ paused: true, count: 2 });
    assert.equal(page.pill().textContent, 'Sign in to sync');

    page.queue({ rejected: [{ kind: 'attempt', message: 'Boulder was deleted', at: 1 }] });
    const pill = page.pill();
    assert.equal(pill.textContent, '1 change could not be uploaded');
    assert.equal(pill.tagName, 'BUTTON');
    pill.click();
    const panel = page.body.all().find(e => e.className === 'bw-conn-panel');
    assert.match(panel.textContent, /attempt: Boulder was deleted/);
    const dismiss = panel.all().find(e => e.tagName === 'BUTTON' && e.textContent === 'Dismiss');
    dismiss.click();
    assert.equal(page.win.bwStatus.current(), null, 'dismissing the rejected actions takes the conflict down');
    assert.equal(page.pill().textContent, 'Sign in to sync');
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
