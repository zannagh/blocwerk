/*
 * Reveals a section of the page: opens every <details> it sits in (a fragment link alone does not
 * reliably expand a closed card in every browser) and scrolls it into view.
 */
export function reveal(id) {
    const target = document.getElementById(id);
    if (!target) {
        revealLazy(id);
        return;
    }

    let details = target.closest('details');
    while (details) {
        details.open = true;
        details = details.parentElement ? details.parentElement.closest('details') : null;
    }

    requestAnimationFrame(() => target.scrollIntoView({ behavior: 'smooth', block: 'start' }));
}

/*
 * A lazily built card (LazySettingsCard) names the anchors its body will hold in data-reveal. Opening it builds the
 * body on the server; reveal once the anchor has arrived, or give up after a few seconds.
 */
function revealLazy(id) {
    const card = [...document.querySelectorAll('details[data-reveal]')]
        .find(d => d.dataset.reveal.split(/\s+/).includes(id));
    if (!card) {
        return;
    }

    card.open = true;
    const until = performance.now() + 4000;
    const wait = () => {
        if (document.getElementById(id)) {
            reveal(id);
        } else if (performance.now() < until) {
            requestAnimationFrame(wait);
        }
    };
    requestAnimationFrame(wait);
}

/*
 * Scrolls an element just far enough to be fully visible, honouring its CSS scroll-margin (focus()
 * alone does not scroll an element that is already on screen, even if a fixed banner covers it).
 */
export function revealElement(element) {
    if (element && typeof element.scrollIntoView === 'function') {
        element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
}

/*
 * Scrolls an element to the middle of the viewport (an inline confirmation that opened near the
 * bottom of a short screen); CSS scroll-margin keeps it clear of the tab bar and banners.
 */
export function revealCentered(element) {
    if (element && typeof element.scrollIntoView === 'function') {
        element.scrollIntoView({ block: 'center', inline: 'nearest' });
    }
}
