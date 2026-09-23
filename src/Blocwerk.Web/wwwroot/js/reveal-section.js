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
