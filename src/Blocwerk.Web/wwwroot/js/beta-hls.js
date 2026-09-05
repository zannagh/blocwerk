/*
 * bwBetaHls — adaptive (ABR) playback for beta videos.
 *
 * attach(videoEl, masterUrl, fallbackMp4Url):
 *   - Safari/iOS, where the browser plays HLS natively, point the <video> straight at the master
 *     playlist. hls.js is NOT needed or loaded there.
 *   - Everyone else lazy-loads the vendored hls.min.js (once, cached across opens) and plays through
 *     MediaSource. hls.js adapts the rendition to the measured bandwidth on its own — that IS the ABR,
 *     so there is no manual level wiring here.
 *   - Any failure along the way — the script won't load (flaky WiFi), MSE isn't supported, or an
 *     unrecoverable fatal hls.js error — drops the element to the progressive 720p MP4 so a clip
 *     always plays.
 *
 * attach() returns a handle whose destroy() tears the player down: it destroys the hls.js instance
 * (if any) and clears the element's source so no background segment fetch outlives the clip. Call it
 * when the clip changes or the lightbox closes — important on a kiosk that stays open for hours.
 *
 * The big hls.min.js is loaded on demand from here, so this small glue file is safe to reference
 * normally in the page. Everything is wrapped so a JS error can never leave a dead player with no
 * fallback.
 */
(function () {
    'use strict';

    var HLS_SCRIPT_URL = '/lib/hls/hls.min.js';
    var HLS_MIME = 'application/vnd.apple.mpegurl';
    var MAX_RECOVERIES = 2;

    // Load the ~415 KB hls.min.js exactly once. Concurrent or repeat opens share this one promise so
    // the file is never fetched twice.
    var scriptPromise = null;

    function loadHlsScript() {
        if (scriptPromise) {
            return scriptPromise;
        }

        scriptPromise = new Promise(function (resolve, reject) {
            if (window.Hls) {
                resolve(window.Hls);
                return;
            }

            var script = document.createElement('script');
            script.src = HLS_SCRIPT_URL;
            script.async = true;
            script.onload = function () {
                if (window.Hls) {
                    resolve(window.Hls);
                } else {
                    reject(new Error('hls.js loaded but window.Hls is missing'));
                }
            };
            script.onerror = function () {
                // A failed load must not poison later attempts: forget the promise so the next open
                // (perhaps on a better connection) can retry the fetch.
                scriptPromise = null;
                reject(new Error('Failed to load ' + HLS_SCRIPT_URL));
            };
            document.head.appendChild(script);
        });

        return scriptPromise;
    }

    // Autoplay may be refused (no user gesture, low-power mode). The controls are visible either way,
    // so a refusal is swallowed — matching beta-video.js.
    function playSilently(videoEl) {
        try {
            var started = videoEl.play();
            if (started && typeof started.catch === 'function') {
                started.catch(function () { });
            }
        } catch (e) {
            // Nothing to do — the user can hit play.
        }
    }

    function fallbackToMp4(videoEl, fallbackMp4Url) {
        try {
            if (fallbackMp4Url && videoEl.src !== fallbackMp4Url) {
                videoEl.src = fallbackMp4Url;
                videoEl.load();
                playSilently(videoEl);
            }
        } catch (e) {
            // Truly nothing left to try.
        }
    }

    function attach(videoEl, masterUrl, fallbackMp4Url) {
        var hls = null;
        var destroyed = false;
        var netRecoveries = 0;
        var mediaRecoveries = 0;

        var handle = {
            destroy: function () {
                destroyed = true;
                try {
                    if (hls) {
                        hls.destroy();
                        hls = null;
                    }
                } catch (e) {
                    // Already gone.
                }
                try {
                    // Clear the source so buffered data is dropped and any in-flight segment/MP4
                    // fetch is aborted; without this the browser keeps pulling bytes after close.
                    videoEl.removeAttribute('src');
                    videoEl.load();
                } catch (e) {
                    // Element may already be detached.
                }
            }
        };

        try {
            if (!videoEl || !masterUrl) {
                return handle;
            }

            // 1) Native HLS (Safari/iOS): let the browser play the master playlist directly.
            if (videoEl.canPlayType && videoEl.canPlayType(HLS_MIME)) {
                var onNativeError = function () {
                    videoEl.removeEventListener('error', onNativeError);
                    if (!destroyed) {
                        fallbackToMp4(videoEl, fallbackMp4Url);
                    }
                };
                videoEl.addEventListener('error', onNativeError);
                videoEl.src = masterUrl;
                playSilently(videoEl);
                return handle;
            }

            // 2) hls.js over MediaSource. Lazy-load, then wire up; any failure falls back to the MP4.
            loadHlsScript().then(function (Hls) {
                if (destroyed) {
                    return;
                }

                if (!Hls || !Hls.isSupported()) {
                    fallbackToMp4(videoEl, fallbackMp4Url);
                    return;
                }

                hls = new Hls({ enableWorker: true, lowLatencyMode: false });

                hls.on(Hls.Events.ERROR, function (event, data) {
                    if (!data || !data.fatal) {
                        // Non-fatal: hls.js recovers these on its own.
                        return;
                    }

                    // Try to recover the recoverable classes a bounded number of times before giving
                    // up — a persistently failing stream must not loop forever re-loading.
                    if (data.type === Hls.ErrorTypes.NETWORK_ERROR && netRecoveries < MAX_RECOVERIES) {
                        netRecoveries++;
                        try {
                            hls.startLoad();
                            return;
                        } catch (e) {
                            // Fall through to give up.
                        }
                    } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR && mediaRecoveries < MAX_RECOVERIES) {
                        mediaRecoveries++;
                        try {
                            hls.recoverMediaError();
                            return;
                        } catch (e) {
                            // Fall through to give up.
                        }
                    }

                    // Unrecoverable: tear the instance down and drop to the progressive MP4.
                    try {
                        hls.destroy();
                    } catch (e) {
                        // Already gone.
                    }
                    hls = null;
                    if (!destroyed) {
                        fallbackToMp4(videoEl, fallbackMp4Url);
                    }
                });

                hls.on(Hls.Events.MANIFEST_PARSED, function () {
                    // Keep the lightbox's autoplay behaviour: start once the manifest is ready.
                    if (!destroyed) {
                        playSilently(videoEl);
                    }
                });

                hls.loadSource(masterUrl);
                hls.attachMedia(videoEl);
            }).catch(function () {
                // Script failed to load or a wiring error: still give the viewer a playable clip.
                if (!destroyed) {
                    fallbackToMp4(videoEl, fallbackMp4Url);
                }
            });

            return handle;
        } catch (e) {
            // A bug in here must never leave a dead player with no fallback.
            fallbackToMp4(videoEl, fallbackMp4Url);
            return handle;
        }
    }

    window.bwBetaHls = { attach: attach };
})();
