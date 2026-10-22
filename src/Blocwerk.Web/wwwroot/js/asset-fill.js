/*
 * bwAssetFill — runs the background fill of the wall-asset cache (design 2026-10-21 §6.8). asset-plan.js decides what.
 *
 * The page fetches each planned asset with `priority: 'low'`; the service worker (sw-asset-cache.js) stores it in the
 * partition's cache and posts `asset-cached`, or `asset-refused` when the browser refused the write. This file never
 * writes the Cache API itself, and it never buffers a body: the response is drained chunk by chunk and dropped.
 *
 * A RUN holds the 'bw-fill' lock (one filling tab) and ends, releasing it, as soon as any of these happens:
 *   'hidden'    the page went to the background (the caller resumes on visibilitychange);
 *   'offline'   a fetch failed while the browser says it is offline (the caller resumes on `online`; the next run's
 *               plan includes that task again). A fetch that fails while online is that task's failure: it is retried
 *               on later runs and skipped for the page's life after FAILURE_LIMIT failures; the run goes on;
 *   'flaky'     STREAK_LIMIT fetches failed in a row while online: the network, not the assets (their failures are
 *               taken back); the caller retries on a backoff timer;
 *   'unsure'    UNSURE_LIMIT assets in a row were neither confirmed stored nor refused (a slow worker), or the worker
 *               serves another partition than this page's ('mismatch' from the caller): try later, backing off;
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
    const FAILURE_LIMIT = 3;
    // This many fetches failing in a row while the browser says it is online (a wifi blip, a captive portal) end the
    // run as 'flaky', so one outage cannot burn every task's FAILURE_LIMIT.
    const STREAK_LIMIT = 3;
    const RETRY_MS = 30 * 1000;
    const BACKOFF_CAP_MS = 15 * 60 * 1000;
    // After this many 'unsure' (or 'mismatch') runs the page gives up filling: a worker that never confirms would
    // otherwise re-download the same (up to 400 MB) assets on every retry.
    const UNSURE_RUNS_LIMIT = 5;

    /** The wait before the n-th 'unsure' retry (1-based): 30 s, 1 min, 2 min, 4 min, ... capped at 15 min. */
    function backoffMs(n) {
        return Math.min(BACKOFF_CAP_MS, RETRY_MS * 2 ** Math.max(0, n - 1));
    }

    /**
     * A stopped run resumes on what stopped it: the page coming back ('hidden'), the network coming back ('offline'),
     * or a timer: 'locked' after RETRY_MS, 'unsure'/'mismatch' after backoffMs and not at all past UNSURE_RUNS_LIMIT
     * (`holder.stopped`), 'flaky' after backoffMs on its own count and never giving up. One timer at a time, held in
     * `holder.retry`; `holder.unsureRuns` / `holder.flakyRuns` count, a 'done' resets both.
     */
    function resumeAfter(win, result, again, holder) {
        if (result === 'done') {
            holder.unsureRuns = 0;
            holder.flakyRuns = 0;
            return;
        }
        if (result === 'flaky') {
            holder.flakyRuns = (holder.flakyRuns || 0) + 1;
        }
        if (result === 'unsure' || result === 'mismatch') {
            holder.unsureRuns = (holder.unsureRuns || 0) + 1;
            if (holder.unsureRuns >= UNSURE_RUNS_LIMIT) {
                holder.stopped = true;
                return;
            }
        }
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
        } else if (['locked', 'unsure', 'mismatch', 'flaky'].includes(result) && !holder.retry) {
            const runs = result === 'flaky' ? holder.flakyRuns : holder.unsureRuns;
            const wait = result === 'locked' ? RETRY_MS : backoffMs(runs);
            holder.retry = win.setTimeout(() => { holder.retry = null; again(); }, wait);
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
     *        hidden() → bool, online() → bool, forget(task) (drops an earlier announcement of the task's key),
     *        concurrency() → n, freeOneWall() → Promise<bool>, lock(fn) → Promise }
     * Returns { run(tasks) → Promise<'done'|'hidden'|'offline'|'unsure'|'streaming'|'locked'>, streaming }.
     */
    function createRunner(env) {
        let streaming = false;
        let retried = false;
        const failures = new Map();
        const online = () => (typeof env.online === 'function' ? env.online() : true);

        // Throws on a network failure; anything the server answered is an outcome.
        async function one(task) {
            const label = env.partition();
            // An announcement from before this fetch (an earlier store that was evicted since) must not confirm it.
            if (typeof env.forget === 'function') {
                env.forget(task);
            }
            const response = await env.fetch(task.url, { priority: 'low', credentials: 'same-origin' });
            // A 404/403 (the asset or the access went): the manifest's next fetch drops it; until then it counts
            // against the asset's failure budget, so it is not fetched again on every pass. Any other answer is skipped.
            if (response.status === 404 || response.status === 403) {
                return 'gone';
            }
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
            const queue = tasks.filter(task => (failures.get(task.key) || 0) < FAILURE_LIMIT);
            let stop = null;
            let unsure = 0;
            const streak = [];

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
                        if (!online()) {
                            // Nothing was recorded for it, so the next run (on `online`) plans it again.
                            stop = stop || 'offline';
                            break;
                        }
                        failures.set(task.key, (failures.get(task.key) || 0) + 1);
                        streak.push(task.key);
                        if (streak.length >= STREAK_LIMIT) {
                            // A run of failures in a row is the network, not these assets: give their budget back.
                            streak.forEach(key => failures.set(key, failures.get(key) - 1));
                            stop = stop || 'flaky';
                        }
                        continue;
                    }
                    streak.length = 0;
                    if (outcome === 'gone') {
                        failures.set(task.key, (failures.get(task.key) || 0) + 1);
                    } else if (outcome === 'refused') {
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

    return { UNSURE_LIMIT, FAILURE_LIMIT, STREAK_LIMIT, UNSURE_RUNS_LIMIT, RETRY_MS, backoffMs, concurrencyOf, lock, discard, resumeAfter, createRunner };
})();
