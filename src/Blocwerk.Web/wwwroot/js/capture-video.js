// Capture walk-along video upload: streams the picked file as the raw request body via XHR (never over
// the SignalR circuit, never read into memory here), reporting upload progress back to .NET.
window.bwCaptureVideo = {
    /** Resolves to { ok, error }. */
    upload(inputId, url, dotNetRef) {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) {
            return Promise.resolve({ ok: false, error: 'No video selected.' });
        }

        return new Promise((resolve) => {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', url + (url.includes('?') ? '&' : '?') + 'name=' + encodeURIComponent(file.name), true);
            xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream');
            // A 2 GB walk over a slow uplink takes a while; the server then only probes the file. The
            // .NET side waits a little longer than this (WallCaptureVideoUpload.UploadTimeout).
            xhr.timeout = 120 * 60 * 1000;
            const started = performance.now();
            let lastPct = -1;
            let lastReport = 0;
            xhr.upload.onprogress = (e) => {
                if (!e.lengthComputable) {
                    return;
                }
                const now = performance.now();
                const pct = Math.floor((e.loaded / e.total) * 100);
                // At most about once a second (plus every whole percent), so a fast LAN upload does
                // not flood the circuit.
                if (pct === lastPct && now - lastReport < 1000) {
                    return;
                }
                lastPct = pct;
                lastReport = now;
                const elapsed = (now - started) / 1000;
                // Average rate since the start: steadier than the last interval on a bursty uplink.
                const eta = elapsed >= 3 && e.loaded > 0
                    ? Math.round((e.total - e.loaded) / (e.loaded / elapsed))
                    : null;
                dotNetRef.invokeMethodAsync('OnVideoUploadProgress', pct, e.loaded / 1048576, e.total / 1048576, eta);
            };
            xhr.onload = () => {
                const ok = xhr.status >= 200 && xhr.status < 300;
                let error = null;
                if (!ok) {
                    error = xhr.responseText || ('Upload failed (HTTP ' + xhr.status + ').');
                    try {
                        const parsed = JSON.parse(error);
                        error = typeof parsed === 'string' ? parsed : (parsed.detail || parsed.title || error);
                    } catch { /* plain text */ }
                }
                if (input) {
                    input.value = '';
                }
                resolve({ ok, error });
            };
            xhr.onerror = () => resolve({ ok: false, error: 'Network error during upload.' });
            xhr.ontimeout = () => resolve({ ok: false, error: 'The upload timed out.' });
            xhr.send(file);
        });
    },
};
