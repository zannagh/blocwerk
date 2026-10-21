/*
 * The storage probe's quick checks and markers (window.bwProbe.checks):
 *
 *  - quick(): persisted() before and after persist(), estimate(), the UA, standalone, device hints and which
 *    storage APIs exist (questions 1 and 4);
 *  - writeMarkers() / checkMarkers(): one timestamped marker in each of localStorage, a cookie, IndexedDB,
 *    the Cache API and OPFS, read back on a later visit, so a wipe shows up as exactly which stores lost
 *    theirs (questions 2 and 3). The Safari tab and the home-screen app keep separate storage on iOS 17+,
 *    so each context reads only the markers it wrote itself.
 */
(function () {
    'use strict';

    const probe = window.bwProbe;
    const core = probe.core;
    const KEY = 'bw-storage-probe-marker';
    const CACHE = 'bw-storage-probe';
    const CACHE_URL = '/__storage-probe/marker';
    const OPFS_NAME = 'bw-storage-probe-marker.json';
    const STORES = ['localStorage', 'cookie', 'indexedDB', 'cache', 'opfs'];

    async function persisted() {
        try {
            return navigator.storage && navigator.storage.persisted ? await navigator.storage.persisted() : 'unsupported';
        } catch (e) {
            return 'error: ' + probe.describe(e);
        }
    }

    async function workerFeatures() {
        const w = probe.startWorker();
        if (!w) {
            return { worker: false };
        }
        const init = await Promise.race([w.ready, new Promise(r => setTimeout(() => r({ ok: false, error: 'timeout' }), 4000))]);
        return { worker: !!init.ok, syncAccessHandle: !!init.syncAccessHandle, workerError: init.error || null };
    }

    async function quick() {
        const before = await persisted();
        let persistResult;
        try {
            persistResult = navigator.storage && navigator.storage.persist ? await navigator.storage.persist() : 'unsupported';
        } catch (e) {
            persistResult = 'error: ' + probe.describe(e);
        }
        const after = await persisted();
        const est = await probe.estimate();
        const data = {
            persistedBefore: before,
            persist: persistResult,
            persistedAfter: after,
            quota: est.quota,
            usage: est.usage,
            usageDetails: est.details,
            estimateError: est.error || (est.unsupported ? 'unsupported' : null),
            standaloneMedia: !!(window.matchMedia && window.matchMedia('(display-mode: standalone)').matches),
            navigatorStandalone: window.navigator.standalone === undefined ? null : window.navigator.standalone,
            ua: navigator.userAgent,
            platform: navigator.platform,
            touchPoints: navigator.maxTouchPoints,
            deviceMemory: navigator.deviceMemory === undefined ? null : navigator.deviceMemory,
            cores: navigator.hardwareConcurrency || null,
            jsHeapLimit: performance.memory ? performance.memory.jsHeapSizeLimit : null,
            screen: screen.width + 'x' + screen.height + '@' + window.devicePixelRatio,
            opfs: probe.hasOpfs(),
            createWritable: probe.hasCreateWritable(),
            cacheApi: typeof caches !== 'undefined',
            indexedDB: typeof indexedDB !== 'undefined',
            crossOriginIsolated: !!window.crossOriginIsolated,
        };
        Object.assign(data, await workerFeatures());
        await probe.post('quick', 'ok', data);

        probe.log('persisted() before asking: ' + before + '. persist() answered: ' + persistResult
            + '. persisted() now: ' + after + '.', after === true ? 'ok' : 'warn');
        probe.log('Quota ' + core.formatBytes(est.quota) + ', used ' + core.formatBytes(est.usage) + '.', 'info');
        probe.log('Context: ' + probe.ctx + ' (display-mode standalone ' + data.standaloneMedia
            + ', navigator.standalone ' + data.navigatorStandalone + '). Device memory: '
            + (data.deviceMemory === null ? 'not exposed' : data.deviceMemory + ' GB') + '.', 'info');
        probe.log('OPFS ' + (data.opfs ? 'yes' : 'NO') + ', createWritable ' + (data.createWritable ? 'yes' : 'no')
            + ', sync access handle in a worker ' + (data.syncAccessHandle ? 'yes' : 'no') + '.', 'info');
        probe.log(navigator.userAgent, 'info');
    }

    function readCookie() {
        const match = document.cookie.split('; ').find(c => c.startsWith(KEY + '='));
        return match ? decodeURIComponent(match.slice(KEY.length + 1)) : null;
    }

    async function writeOne(store, text) {
        if (store === 'localStorage') {
            localStorage.setItem(KEY, text);
        } else if (store === 'cookie') {
            const secure = location.protocol === 'https:' ? ';Secure' : '';
            document.cookie = KEY + '=' + encodeURIComponent(text) + ';path=/;max-age=34560000;SameSite=Lax' + secure;
        } else if (store === 'indexedDB') {
            await probe.idbPut(KEY, text);
        } else if (store === 'cache') {
            const cache = await caches.open(CACHE);
            await cache.put(CACHE_URL, new Response(text, { headers: { 'Content-Type': 'application/json' } }));
        } else {
            const result = await probe.opfsWrite(OPFS_NAME, undefined, text);
            if (!result.ok) {
                throw new Error((result.unsupported ? 'unsupported: ' : '') + result.error
                    + (result.writableError ? ' (createWritable: ' + result.writableError + ')' : ''));
            }
        }
    }

    async function readOne(store) {
        if (store === 'localStorage') {
            return localStorage.getItem(KEY);
        }
        if (store === 'cookie') {
            return readCookie();
        }
        if (store === 'indexedDB') {
            return (await probe.idbGet(KEY)) || null;
        }
        if (store === 'cache') {
            const cache = await caches.open(CACHE);
            const hit = await cache.match(CACHE_URL);
            return hit ? await hit.text() : null;
        }
        const file = await probe.opfsFile(OPFS_NAME);
        return file ? await file.text() : null;
    }

    async function writeMarkers() {
        const marker = JSON.stringify({ ctx: probe.ctx, run: probe.runId, at: new Date().toISOString(), persisted: await persisted() });
        const results = {};
        for (const store of STORES) {
            try {
                await writeOne(store, marker);
                results[store] = 'written';
            } catch (e) {
                results[store] = /unsupported/i.test(probe.describe(e)) ? 'unsupported' : 'error: ' + probe.describe(e);
            }
        }
        await probe.post('markers-write', 'ok', { marker: JSON.parse(marker), stores: results });
        const allWritten = STORES.every(s => results[s] === 'written');
        probe.log('Markers written at ' + new Date().toLocaleString() + ' (' + probe.ctx + '): '
            + STORES.map(s => s + ' ' + results[s]).join(', ') + '.', allWritten ? 'ok' : 'warn');
    }

    /** Reads every marker; quiet=true is the automatic read on page load (no on-screen lines). */
    async function checkMarkers(quiet) {
        const now = Date.now();
        const stores = {};
        for (const store of STORES) {
            try {
                const text = await readOne(store);
                const marker = text ? JSON.parse(text) : null;
                stores[store] = marker
                    ? { present: true, ctx: marker.ctx, at: marker.at, ageDays: core.ageDays(marker.at, now), persisted: marker.persisted }
                    : { present: false };
            } catch (e) {
                stores[store] = { present: false, error: probe.describe(e) };
            }
        }
        const previous = probe.previousVisit;
        await probe.post(quiet ? 'markers-load' : 'markers-check', 'ok', {
            stores: stores,
            previousProbeVisit: previous,
            daysSincePreviousVisit: core.ageDays(previous, now),
            persisted: await persisted(),
        });
        if (!quiet) {
            for (const store of STORES) {
                const s = stores[store];
                probe.log(store + ': ' + (s.present
                    ? 'marker present, written ' + s.ageDays + ' days ago by the ' + s.ctx + ' context'
                    : 'NO marker' + (s.error ? ' (' + s.error + ')' : '')), s.present ? 'ok' : 'fail');
            }
            probe.log('Previous visit to this page: ' + (previous ? new Date(previous).toLocaleString() : 'none recorded') + '.', 'info');
        }
        return stores;
    }

    probe.checks = { quick: quick, writeMarkers: writeMarkers, checkMarkers: checkMarkers };
})();
