#!/usr/bin/env python3
"""Patches Spark's decode worker so a level given as `fileBytes` is not read through a stream.

The worker wraps the bytes in a ReadableStream, reads it to the end and calls `releaseLock()` on the
reader. In WebKit (iOS Safari) the worker thread sometimes never returns from that call: no result, no
error, and the worker answers nothing afterwards, so `SplatMesh.initialized` never settles and the
photo-real view stays on the level it shows. Seen on the second level of a view (the first one is
decoded while the worker starts), more often under load.

Fix: bytes that are already there go straight to the decoder, with the same progress status.

Idempotent; asserts the one site, so a Spark upgrade that moves it fails loudly.

Usage: patch-spark-worker.py <spark.module(.min).js> [--check]
"""
import sys

MARKER = '/* bw-worker-bytes */'
SITE = 'sendStatus:c}){let u,d=0;if(t)u=new ReadableStream('
FIX = ('sendStatus:c}){' + MARKER
       + 'if(t){c({loaded:t.length,total:t.length});n.push(t);return n.finish()}'
       + 'let u,d=0;if(t)u=new ReadableStream(')


def main():
    path = sys.argv[1]
    src = open(path, encoding='utf-8').read()
    if MARKER in src:
        print('already patched')
        return
    if src.count(SITE) != 1:
        raise SystemExit(f'expected 1 site, found {src.count(SITE)}: review the new Spark by hand')
    print('patched the worker byte reader')
    if '--check' not in sys.argv:
        open(path, 'w', encoding='utf-8').write(src.replace(SITE, FIX))


if __name__ == '__main__':
    main()
