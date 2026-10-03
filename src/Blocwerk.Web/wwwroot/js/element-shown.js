/*
 * bwShown — whether an element is on screen for a polling component: the tab is visible and the
 * element is laid out (not inside a closed <details>, not display:none). Pollers skip their refresh
 * otherwise, so a hidden panel costs the server nothing.
 */
window.bwShown = function (element) {
    'use strict';
    return document.visibilityState === 'visible' && !!element && element.getClientRects().length > 0;
};
