/*
 * Reveals a section of the page: opens every <details> it sits in (a fragment link alone does not
 * reliably expand a closed card in every browser) and scrolls it into view.
 */
export function reveal(id) {
    const target = document.getElementById(id);
    if (!target) {
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
