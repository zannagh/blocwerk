// The boulder list's search chip (WallDetail.ListFilters.cs). Opening it is a server round trip, and a phone only
// raises its keyboard for a focus that happens inside the tap itself, so this shows the field and focuses it right
// there, before Blazor has heard of the tap. Blazor's own render then agrees (it drops `hidden` too). Closing is left
// to Blazor alone.
(function () {
    'use strict';

    document.addEventListener('click', function (e) {
        const toggle = e.target && e.target.closest ? e.target.closest('[data-bw-search-toggle]') : null;
        if (!toggle || toggle.getAttribute('aria-expanded') === 'true') {
            return;
        }
        const row = document.getElementById(toggle.getAttribute('aria-controls'));
        const input = row ? row.querySelector('input') : null;
        if (!input) {
            return;
        }
        row.hidden = false;
        try {
            input.focus({ preventScroll: true });
        } catch (err) {
            /* focus is a convenience */
        }
    }, true);
})();
