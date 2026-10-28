// Where each page module of the device cache lives on disk. The ledger (and its IndexedDB half), the wipe rules, the
// 401 lock, the partition session and "Clear offline data" are Blocwerk.Shared.Ui's since Phase 3 PR 3.7, so the
// offline client loads the same files; the rest stay in Blocwerk.Web. A name in neither place fails the read.
const web = new URL('../../../src/Blocwerk.Web/wwwroot/js/', import.meta.url);
const sharedUi = new URL('../../../src/Blocwerk.Shared.Ui/wwwroot/js/', import.meta.url);

export const SHARED_UI_MODULES = new Set(['asset-ledger-idb.js', 'asset-ledger.js', 'asset-wipe.js', 'asset-lock.js', 'asset-session.js', 'asset-clear.js']);

/** The file URL of page module `name` (e.g. 'asset-ledger.js'). */
export function moduleUrl(name) {
    return new URL(name, SHARED_UI_MODULES.has(name) ? sharedUi : web);
}
