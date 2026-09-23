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
            // A 1 GB walk over a slow uplink takes a while; the server then only probes the file.
            xhr.timeout = 60 * 60 * 1000;
            let last = -1;
            xhr.upload.onprogress = (e) => {
                if (!e.lengthComputable) {
                    return;
                }
                const pct = Math.floor((e.loaded / e.total) * 100);
                if (pct !== last) {
                    last = pct;
                    dotNetRef.invokeMethodAsync('OnVideoUploadProgress', pct);
                }
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
