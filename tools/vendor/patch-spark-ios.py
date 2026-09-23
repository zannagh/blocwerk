#!/usr/bin/env python3
"""Patches Spark's GLSL so it links on iOS Safari (WebKit's ANGLE -> Metal translator).

The bug (sparkjsdev/spark#399, WebKit 319958): a GLSL call that passes a struct member, a
swizzle or a global (e.g. a fragment output) to an `out` / `inout` parameter is translated to
MSL as `ANGLE_out(splat.center)`; the temporary lives in the `__metal_generic` address space and
cannot bind to the `thread T&` the callee takes, so the program fails to link:

    reference to type 'thread float3' could not bind to an lvalue of type '__metal_generic float3'

Fix: every such call site is rewritten to go through plain locals, e.g.

    unpackSplatEncoding(p, gsplat.center, gsplat.scales, ...);
 -> { vec3 bwIos2; vec3 bwIos3; ...; unpackSplatEncoding(p, bwIos2, bwIos3, ...);
      gsplat.center = bwIos2; gsplat.scales = bwIos3; ... }

`inout` temps are initialised from the original lvalue; `out` temps are not. Only statement
calls are touched; the script refuses anything else. It is deterministic and idempotent, and it
asserts the number of rewritten call sites, so a Spark upgrade that moves them fails loudly.

Usage: patch-spark-ios.py <spark.module(.min).js> [--check]
"""
import re
import sys

MARKER = '/* bw-ios-outargs */'
EXPECTED_SITES = 12
# Globals that are passed bare to `out` parameters (fragment outputs of Spark's pack programs).
GLOBAL_LVALUES = {'target', 'target2'}
PLAIN = re.compile(r'[A-Za-z_]\w*|\$\{[A-Za-z_][\w.]*\}')


def split_args(text):
    args, depth, cur = [], 0, ''
    for ch in text:
        if ch in '([{':
            depth += 1
        elif ch in ')]}':
            depth -= 1
        if ch == ',' and depth == 0:
            args.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        args.append(cur.strip())
    return args


def signatures(src):
    """name -> [(qualifier, type)] for every GLSL function with an out/inout parameter."""
    sigs = {}
    for m in re.finditer(r'\b(?:void|bool|float)\s+(\w+)\s*\(([^()]*)\)\s*\{', src):
        params = []
        for p in split_args(m.group(2)):
            words = p.split()
            qual = words[0] if words[0] in ('out', 'inout') else 'in'
            params.append((qual, words[1] if qual != 'in' else None))
        if any(q != 'in' for q, _ in params):
            sigs[m.group(1)] = params
    return sigs


def rewrite(src):
    sigs = signatures(src)
    edits = []
    for name, params in sigs.items():
        for m in re.finditer(r'(?<![\w.])' + name + r'\s*\(', src):
            if re.search(r'\b(?:void|bool|float)\s+$', src[max(0, m.start() - 12):m.start()]):
                continue                                        # the definition itself
            i, depth = m.end(), 1
            while depth:
                depth += {'(': 1, ')': -1}.get(src[i], 0)
                i += 1
            args = split_args(src[m.end():i - 1])
            bad = [k for k, (a, (q, _)) in enumerate(zip(args, params))
                   if q != 'in' and (not PLAIN.fullmatch(a) or a in GLOBAL_LVALUES)]
            if not bad:
                continue
            end = i
            while src[end] in ' \t\n':
                end += 1
            if src[end] != ';':
                raise SystemExit(f'{name} at {m.start()}: not a statement call, patch by hand')
            before, after, new_args = [], [], list(args)
            for k in bad:
                q, t = params[k]
                tmp = f'bwIos{k}'
                before.append(f'{t} {tmp}' + (f' = {args[k]};' if q == 'inout' else ';'))
                after.append(f'{args[k]} = {tmp};')
                new_args[k] = tmp
            call = f'{name}({", ".join(new_args)});'
            text = '{ ' + MARKER + ' ' + ' '.join(before) + ' ' + call + ' ' + ' '.join(after) + ' }'
            edits.append((m.start(), end + 1, text, name))
    edits.sort()
    for start, end, text, _ in reversed(edits):
        src = src[:start] + text + src[end:]
    return src, [e[3] for e in edits]


def main():
    path = sys.argv[1]
    src = open(path, encoding='utf-8').read()
    if MARKER in src:
        print('already patched')
        return
    out, sites = rewrite(src)
    for s in sites:
        print('rewrote', s)
    if len(sites) != EXPECTED_SITES:
        raise SystemExit(f'expected {EXPECTED_SITES} call sites, found {len(sites)}: review the new Spark by hand')
    if '--check' not in sys.argv:
        open(path, 'w', encoding='utf-8').write(out)


if __name__ == '__main__':
    main()
