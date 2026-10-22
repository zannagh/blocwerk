/*
 * After the server terminates the circuit, every later navigation is a full page load.
 *
 * An unhandled exception on the circuit (the server's "JS.Error") makes Blazor show #blazor-error-ui
 * and stop for good: it marks rendering failed, so the reconnection handler never runs, no
 * components-reconnect-state-changed event fires, and blazor-boot.js still believes the circuit
 * is 'connected'. Enhanced navigation keeps working regardless — it is a plain fetch plus a DOM
 * merge — so the next page arrives as its prerendered HTML, its interactive components are never
 * attached, and anything that loads interactively stays on "Loading…" until the user reloads.
 *
 * So: the error UI turning visible while the connection is not mid-reconnect means the circuit is
 * dead. From then on links opt out of enhanced navigation (data-enhance-nav="false" on <html>, which
 * Blazor's click handler honours via closest()), back/forward reloads, and any enhanced load that
 * still happens (a form post) is followed by a reload. Nothing reloads on its own: a page that
 * crashes while loading would otherwise loop, and the banner already offers Reload.
 *
 * The 'connected' check matters: blazor-boot.js hides the banner again when a reconnect succeeds,
 * and a banner shown during a reconnect attempt is that transient case, not a dead circuit.
 */
(function () {
    const ERROR_UI_ID = 'blazor-error-ui';
    let failed = false;

    function connectionUp() {
        return !window.blocwerkConnection || window.blocwerkConnection.status() === 'connected';
    }

    function markFailed() {
        if (failed) {
            return;
        }
        failed = true;
        document.documentElement.setAttribute('data-enhance-nav', 'false');
    }

    function check(errorUi) {
        if (errorUi.style.display === 'block' && connectionUp()) {
            markFailed();
        }
    }

    function watch() {
        const errorUi = document.getElementById(ERROR_UI_ID);
        if (!errorUi || typeof MutationObserver !== 'function') {
            return;
        }
        new MutationObserver(function () {
            check(errorUi);
        }).observe(errorUi, { attributes: true, attributeFilter: ['style'] });
        check(errorUi);
    }

    // Path plus search, so a back/forward between hash fragments on the same page (an in-page
    // anchor) does not reload: that never goes through enhanced navigation in the first place.
    function pageKey() {
        return location.pathname + location.search;
    }

    let lastPage = pageKey();

    window.addEventListener('popstate', function () {
        const page = pageKey();
        const changed = page !== lastPage;
        lastPage = page;
        if (failed && changed) {
            location.reload();
        }
    });

    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', function () {
            // An enhanced navigation pushes its URL without a popstate; keep the key current.
            lastPage = pageKey();
            if (failed) {
                location.reload();
            }
        });
    }

    window.blocwerkCircuitFailed = function () {
        return failed;
    };

    watch();
})();
