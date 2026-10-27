#!/usr/bin/env node
/*
 * Writes the offline client's own copy of the Server pages' styles (PR 3.11 review): the rules of Web's app, pages,
 * usage-heat and components CSS that style markup the client renders, and nothing else, into
 * src/Blocwerk.Client/wwwroot/css/client-base.css (app, pages, usage-heat) and client-components.css (components), so the
 * shell keeps the Server pages' cascade around Shared.Ui's hold-tap.css. The client does not load Web's stylesheets: a Server CSS edit then
 * changes no client file, so it ships no new client version (OfflineClientShellAssets.Version) and no device downloads
 * the client again for it. Run it deliberately when the client should pick up a Server style change:
 *
 *   node build/subset-client-css.mjs
 *
 * A rule is kept when every class in one of its selectors is a class the client renders (the class attributes of
 * Blocwerk.Client's and Blocwerk.Shared.Ui's .razor files and the Class parameters handed to components, literals and the strings inside
 * @(...) alike, plus EXTRA),
 * or when it is a class-less rule of the page itself (:root, html, body, *, headings, [data-theme]). At-rule blocks
 * (@media, @container, @supports) keep the rules kept inside them; @keyframes are kept when a kept rule names them.
 */
import { readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = new URL('..', import.meta.url).pathname;
const WEB_CSS = f => join(ROOT, 'src/Blocwerk.Web/wwwroot/css', f);
const OUTPUTS = {
    'client-base.css': ['app.css', 'pages.css', 'usage-heat.css'],
    'client-components.css': ['components.css'],
};
const RAZOR_DIRS = ['src/Blocwerk.Client/Pages', 'src/Blocwerk.Client/Views', 'src/Blocwerk.Client/Layout', 'src/Blocwerk.Shared.Ui/Overlay'];
// Classes built from values the class attribute does not spell out (PanelViewer's chevrons, the tiers' `--tier` html).
const EXTRA = ['left', 'right', 'up', 'down', 'active'];
const PAGE_SELECTOR = /^(:root|html|body|\*|h[1-6]\b|a\b|\[data-theme|:where\()/;

export function renderedClasses() {
    const classes = new Set(EXTRA);
    for (const dir of RAZOR_DIRS) {
        for (const file of readdirSync(join(ROOT, dir)).filter(f => f.endsWith('.razor'))) {
            const text = readFileSync(join(ROOT, dir, file), 'utf8');
            for (const value of classAttributes(text)) {
                value.replace(/@\([^]*?\)(?=[\s"]|$)|@[A-Za-z_.]+/g, m => { (m.match(/"([^"]*)"/g) || []).forEach(s => tokens(s.slice(1, -1), classes)); return ' '; })
                    .split(/\s+/).forEach(t => tokens(t, classes));
            }
        }
    }
    return classes;
}

function tokens(text, into) {
    for (const t of text.split(/\s+/)) {
        if (/^-?[a-z_][a-z0-9_-]*$/i.test(t)) {
            into.add(t);
        }
    }
}

// The value of every class="…" attribute, with the quotes inside @( … ) kept in it.
function classAttributes(text) {
    const values = [];
    let at = 0;
    // `class="…"` on an element, and `Class="…"` handed to a component that puts it on one (PanelPhoto).
    const next = from => {
        const found = /\b[cC]lass="/g;
        found.lastIndex = from;
        const m = found.exec(text);
        return m ? m.index + m[0].length - 7 : -1;
    };
    while ((at = next(at)) >= 0) {
        let i = at + 7, depth = 0, quoted = false;
        for (; i < text.length; i++) {
            const c = text[i];
            if (depth > 0 && c === '"') { quoted = !quoted; continue; }
            if (quoted) { continue; }
            if (c === '(' && (text[i - 1] === '@' || depth > 0)) { depth++; continue; }
            if (c === ')' && depth > 0) { depth--; continue; }
            if (c === '"' && depth === 0) { break; }
        }
        values.push(text.slice(at + 7, i));
        at = i + 1;
    }
    return values;
}

// The CSS as a list of { prelude, body } blocks (body null for a statement), comments dropped.
function parse(css) {
    css = css.replace(/\/\*[^]*?\*\//g, '');
    const out = [];
    let i = 0;
    while (i < css.length) {
        const open = css.indexOf('{', i);
        const semi = css.indexOf(';', i);
        if (open < 0) { break; }
        if (semi >= 0 && semi < open && css.slice(i, semi).trim().startsWith('@')) {
            out.push({ prelude: css.slice(i, semi).trim(), body: null });
            i = semi + 1;
            continue;
        }
        let depth = 1, j = open + 1;
        for (; j < css.length && depth > 0; j++) {
            if (css[j] === '{') { depth++; } else if (css[j] === '}') { depth--; }
        }
        out.push({ prelude: css.slice(i, open).trim(), body: css.slice(open + 1, j - 1) });
        i = j;
    }
    return out;
}

// A selector list split at its top-level commas only (`:where(a, b)` stays one selector).
function splitTopLevel(list) {
    const parts = [];
    let depth = 0, start = 0;
    for (let i = 0; i < list.length; i++) {
        if (list[i] === '(') { depth++; } else if (list[i] === ')') { depth--; } else if (list[i] === ',' && depth === 0) {
            parts.push(list.slice(start, i));
            start = i + 1;
        }
    }
    parts.push(list.slice(start));
    return parts;
}

function keepSelector(selector, classes) {
    // A class inside :not(...) is one the element must NOT have, so the client need not render it.
    const used = selector.replace(/:not\([^()]*\)/g, '').match(/\.-?[a-z_][a-z0-9_-]*/gi) || [];
    if (used.length === 0) {
        return PAGE_SELECTOR.test(selector.trim()) && !/#/.test(selector);
    }
    return used.every(c => classes.has(c.slice(1))) && !/#/.test(selector);
}

function subset(blocks, classes, animations) {
    const kept = [];
    for (const { prelude, body } of blocks) {
        if (body === null) { continue; }
        if (/^@(media|container|supports|layer)\b/.test(prelude)) {
            const inner = subset(parse(body), classes, animations);
            if (inner.length) { kept.push(`${prelude} {\n${inner.map(r => '    ' + r.replace(/\n/g, '\n    ')).join('\n')}\n}`); }
            continue;
        }
        if (prelude.startsWith('@')) { continue; }
        const selectors = splitTopLevel(prelude).map(s => s.trim()).filter(s => keepSelector(s, classes));
        if (selectors.length) {
            const decls = body.trim().replace(/\s*\n\s*/g, ' ');
            (decls.match(/animation(?:-name)?\s*:\s*([a-z0-9_-]+)/gi) || []).forEach(a => animations.add(a.split(':')[1].trim().split(/\s+/)[0]));
            kept.push(`${selectors.join(',\n')} { ${decls} }`);
        }
    }
    return kept;
}

export function build(sources) {
    const classes = renderedClasses();
    const animations = new Set();
    const parts = [];
    for (const file of sources.map(WEB_CSS)) {
        const blocks = parse(readFileSync(file, 'utf8'));
        const rules = subset(blocks, classes, animations);
        const frames = blocks.filter(b => b.body !== null && /^@keyframes\s/.test(b.prelude));
        parts.push(`/* ---- from Web's css/${file.split('/').pop()} ---- */`, ...rules, ...frames
            .filter(b => animations.has(b.prelude.split(/\s+/)[1]))
            .map(b => `${b.prelude} {${b.body}}`));
    }
    return '/* GENERATED by build/subset-client-css.mjs from the Server pages\' stylesheets: the rules that style the offline\n' +
        '   client\'s markup. Do not edit; rerun the script to pick up a Server style change (a new client version). */\n' +
        parts.join('\n') + '\n';
}

if (process.argv[1] && process.argv[1].endsWith('subset-client-css.mjs')) {
    for (const [name, sources] of Object.entries(OUTPUTS)) {
        const out = join(ROOT, 'src/Blocwerk.Client/wwwroot/css', name);
        writeFileSync(out, build(sources));
        console.log(`wrote ${out}`);
    }
}
