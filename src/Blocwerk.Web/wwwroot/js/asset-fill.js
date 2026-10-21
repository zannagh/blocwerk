/*
 * bwAssetFill — runs the background fill of the wall-asset cache (design 2026-10-21 §6.8). asset-plan.js decides what.
 *
 * The page fetches each planned asset with `priority: 'low'`; the service worker (sw-asset-cache.js) stores it in the
 * partition's cache and posts `asset-cached`, or `asset-refused` when the browser refused the write. This file never
 * writes the Cache API itself, and it never buffers a body: the response is drained chunk by chunk and dropped.
 *
 * A RUN holds the 'bw-fill' lock (one filling tab) and ends, releasing it, as soon as any of these happens:
 *   'hidden'    the page went to the background (the caller resumes on visibilitychange);
 *   'offline'   a fetch failed (the caller resumes on `online`; the next run's plan includes that task again);
 *   'unsure'    UNSURE_LIMIT assets in a row were neither confirmed stored nor refused (a slow worker): try later;
 *   'streaming' a refusal survived freeing one wall and one retry: no fill again for this page, everything streams;
 *   'done'      the queue is empty.
 * A run that could not get the lock answers 'locked'. Workers stop between tasks, and run() resolves only after every
 * worker has stopped, so no fetch outlives its run or its lock.
 *
 * Only a confirmed refusal counts as one: a verify that times out ('unknown') is not a quota error.
 */
window.bwAssetFill = (function () {
    'use strict';

    const UNSURE_LIMIT = 3;
    const RETRY_MS = 30 * 1000;

    /**
     * A stopped run resumes on what stopped it: the page coming back ('hidden'), the network coming back ('offline'),
     * or a timer ('locked', 'unsure'; one at a time, held in `holder.retry`).
     */
    function resumeAfter(win, result, again, holder) {
        if (result === 'hidden') {
            const onVisible = () => {
                if (win.document.visibilityState !== 'hidden') {
                    win.document.removeEventListener('visibilitychange', onVisible);
                    again();
                }
            };
            win.document.addEventListener('visibilitychange', onVisible);
        } else if (result === 'offline') {
            win.addEventListener('online', () => again(), { once: true });
        } else if ((result === 'locked' || result === 'unsure') && !holder.retry) {
            holder.retry = win.setTimeout(() => { holder.retry = null; again(); }, RETRY_MS);
        }
    }

    // One filling tab: another tab holding 'bw-fill' means this one skips its run.
    function lock(nav, fn) {
        const locks = nav.locks;
        if (!locks || typeof locks.request !== 'function') {
            return fn();
        }
        return locks.request('bw-fill', { ifAvailable: true }, held => (held ? fn() : null));
    }

    function concurrencyOf(nav) {
        return window.bwAssetPlan.isMetered(nav) ? 1 : 2;
    }

    async function discard(response) {
        const body = response.body;
        if (body && typeof body.getReader === 'function') {
            const reader = body.getReader();
            while (!(await reader.read()).done) {
                /* chunks dropped as they arrive */
            }
            return;
        }
        if (typeof response.arrayBuffer === 'function') {
            await response.arrayBuffer();
        }
    }

    /**
     * env: { fetch(url, init), verify(task) → Promise<'stored'|'refused'|'unknown'>, partition() → label,
     *        hidden() → bool, concurrency() → n, freeOneWall() → Promise<bool>, lock(fn) → Promise }
     * Returns { run(tasks) → Promise<'done'|'hidden'|'offline'|'unsure'|'streaming'|'locked'>, streaming }.
     */
    function createRunner(env) {
        let streaming = false;
        let retried = false;

        // Throws on a network failure; anything the server answered is an outcome.
        async function one(task) {
            const label = env.partition();
            const response = await env.fetch(task.url, { priority: 'low', credentials: 'same-origin' });
            // A 404/403 (the asset or the access went) is the manifest's business: its next fetch drops it.
            if (!response.ok) {
                return 'skip';
            }
            await discard(response);
            if (env.partition() !== label) {
                return 'skip';
            }
            return env.verify(task);
        }

        async function run(tasks) {
            if (streaming) {
                return 'streaming';
            }
            const queue = tasks.slice();
            let stop = null;
            let unsure = 0;

            async function drain() {
                while (!stop && queue.length > 0) {
                    if (env.hidden()) {
                        stop = 'hidden';
                        break;
                    }
                    const task = queue.shift();
                    let outcome;
                    try {
                        outcome = await one(task);
                        if (outcome === 'refused' && !retried && await env.freeOneWall()) {
                            retried = true;
                            outcome = await one(task);
                        }
                    } catch (e) {
                        // Nothing was recorded for it, so the next run (on `online`) plans it again.
                        stop = stop || 'offline';
                        break;
                    }
                    if (outcome === 'refused') {
                        streaming = true;
                        stop = 'streaming';
                    } else if (outcome === 'unknown') {
                        unsure += 1;
                        if (unsure >= UNSURE_LIMIT) {
                            stop = stop || 'unsure';
                        }
                    } else if (outcome === 'stored') {
                        unsure = 0;
                    }
                }
            }

            const result = await env.lock(async () => {
                const workers = Math.max(1, env.concurrency());
                await Promise.all(Array.from({ length: workers }, drain));
                return stop || 'done';
            });
            return result || 'locked';
        }

        return { run, get streaming() { return streaming; } };
    }

    return { UNSURE_LIMIT, RETRY_MS, concurrencyOf, lock, discard, resumeAfter, createRunner };
})();
