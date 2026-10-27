/*
 * bwStatusPill — draws the connection / sync pill and its panel (offline-status.js decides what they say).
 *
 * Updated in place: the pill and the panel are kept and only their text, colour and attributes change, so a bubble
 * coming or going every few seconds does not take keyboard focus away from a panel button. The panel is built again
 * only when what it lists changes, and then focus goes back to the button of the same name (or to the pill). A pill
 * that is not a button (nothing to open) still takes focus programmatically (tabindex=-1), so focus that falls back on
 * it never ends on an element that cannot hold it.
 *
 * draw(container, view): view is null (nothing to show) or
 *   { tone, label, bubble (a bwStatus kind, or null), openable, expanded, onToggle, badge (rejected count),
 *     panel: null | { detail, errors: [string], actions: [{ key, label, run } | { key, label, href }] } }
 */
window.bwStatusPill = (function (doc) {
    'use strict';

    function part(tag, className, text) {
        const el = doc.createElement(tag);
        el.className = className;
        if (text !== undefined) {
            el.textContent = text;
        }
        return el;
    }

    function newPill(openable) {
        const pill = doc.createElement(openable ? 'button' : 'div');
        if (openable) {
            pill.type = 'button';
            pill.addEventListener('click', () => pill.bwToggle && pill.bwToggle());
        } else {
            pill.setAttribute('tabindex', '-1');
        }
        pill.appendChild(part('span', 'bw-conn-dot'));
        pill.appendChild(part('span', 'bw-conn-label'));
        return pill;
    }

    function badgeOf(pill, count) {
        let badge = pill.querySelector('.bw-conn-badge');
        if (!count) {
            if (badge) {
                badge.remove();
            }
            return;
        }
        if (!badge) {
            badge = pill.appendChild(part('span', 'bw-conn-badge'));
        }
        badge.textContent = String(count);
        badge.setAttribute('aria-label', count + (count === 1 ? ' change' : ' changes') + ' could not be uploaded');
    }

    function drawPill(container, view) {
        let pill = container.querySelector('.bw-conn-pill');
        let hadFocus = false;
        if (pill && (pill.tagName === 'BUTTON') !== !!view.openable) {
            hadFocus = doc.activeElement === pill;
            pill.remove();
            pill = null;
        }
        if (!pill) {
            pill = container.appendChild(newPill(view.openable));
            if (hadFocus) {
                pill.focus();
            }
        }
        pill.className = 'bw-conn-pill bw-conn-' + view.tone + (view.bubble ? ' bw-status-bubble' : '') + (view.flash ? ' bw-conn-flash' : '');
        if (view.bubble) {
            pill.setAttribute('data-bw-status', view.bubble);
        } else {
            pill.removeAttribute('data-bw-status');
        }
        if (view.openable) {
            pill.setAttribute('aria-expanded', view.expanded ? 'true' : 'false');
            pill.bwToggle = view.onToggle;
        }
        const label = pill.querySelector('.bw-conn-label');
        if (label.textContent !== view.label) {
            label.textContent = view.label;
        }
        badgeOf(pill, view.badge);
        return pill;
    }

    function action(item) {
        if (item.href) {
            const link = part('a', 'bw-conn-btn', item.label);
            link.href = item.href;
            link.setAttribute('data-bw-key', item.key);
            return link;
        }
        const el = part('button', 'bw-conn-btn', item.label);
        el.type = 'button';
        el.setAttribute('data-bw-key', item.key);
        el.addEventListener('click', event => {
            event.stopPropagation();
            item.run();
        });
        return el;
    }

    function buildPanel(spec) {
        const panel = part('div', 'bw-conn-panel');
        panel.appendChild(part('p', 'bw-conn-panel-text', spec.detail));
        spec.errors.forEach(text => panel.appendChild(part('p', 'bw-conn-panel-error', text)));
        const actions = panel.appendChild(part('div', 'bw-conn-panel-actions'));
        spec.actions.forEach(item => actions.appendChild(action(item)));
        return panel;
    }

    // What the panel lists; its sentence is updated in place.
    const signatureOf = spec => JSON.stringify([spec.errors, spec.actions.map(a => a.key + '|' + a.label)]);

    function drawPanel(container, spec, pill) {
        const old = container.querySelector('.bw-conn-panel');
        const signature = spec ? signatureOf(spec) : null;
        if (old && old.bwSignature === signature) {
            const text = old.querySelector('.bw-conn-panel-text');
            if (text.textContent !== spec.detail) {
                text.textContent = spec.detail;
            }
            return;
        }
        const focused = old && doc.activeElement && old.contains(doc.activeElement) ? doc.activeElement.getAttribute('data-bw-key') : null;
        if (old) {
            old.remove();
        }
        if (!spec) {
            if (focused && pill) {
                pill.focus();
            }
            return;
        }
        const panel = container.appendChild(buildPanel(spec));
        panel.bwSignature = signature;
        if (focused) {
            const again = panel.querySelector(`[data-bw-key="${focused}"]`);
            (again || pill).focus();
        }
    }

    function draw(container, view) {
        if (!view) {
            container.textContent = '';
            container.classList.remove('bw-conn-active');
            return;
        }
        container.classList.add('bw-conn-active');
        const pill = drawPill(container, view);
        drawPanel(container, view.openable && view.expanded ? view.panel : null, pill);
    }

    return { draw };
})(document);
