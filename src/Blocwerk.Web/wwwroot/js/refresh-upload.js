// "Update panels + 3D" drop zone: uploads every picked or dropped file as the raw request body via XHR (never over
// the SignalR circuit), a few at a time, reporting overall progress and each file's outcome back to .NET.
window.bwRefreshUpload = {
    /**
     * Resolves to the number of files sent once all are done. Photos past `photoRoom` (what the capture still takes) are
     * refused here, before a byte is sent; `photoLimit` is the capture's limit, for the message.
     */
    upload(inputId, url, dotNetRef, photoRoom, photoLimit) {
        const input = document.getElementById(inputId);
        const picked = input && input.files ? Array.from(input.files) : [];
        if (picked.length === 0) {
            return Promise.resolve(0);
        }

        const isVideo = (f) => /\.(mp4|mov|m4v)$/i.test(f.name) || (f.type || '').startsWith('video/');
        let room = typeof photoRoom === 'number' ? photoRoom : Infinity;
        const files = picked.filter((f) => {
            if (isVideo(f) || room > 0) {
                room -= isVideo(f) ? 0 : 1;
                return true;
            }
            dotNetRef.invokeMethodAsync('OnFileUploaded', {
                photoId: null, fileName: f.name, isVideo: false,
                problem: 'Not sent: a capture takes at most ' + photoLimit + ' photos.',
            }).catch(() => { /* the circuit went away */ });
            return false;
        });
        if (files.length === 0) {
            input.value = '';
            return Promise.resolve(0);
        }

        const total = files.reduce((sum, f) => sum + f.size, 0);
        const loaded = new Array(files.length).fill(0);
        let next = 0;
        let lastReport = 0;
        const report = (force) => {
            const now = performance.now();
            if (!force && now - lastReport < 500) {
                return;
            }
            lastReport = now;
            const sent = loaded.reduce((a, b) => a + b, 0);
            dotNetRef.invokeMethodAsync('OnUploadProgress', Math.floor((sent / Math.max(total, 1)) * 100), sent / 1048576, total / 1048576)
                .catch(() => { /* the circuit went away */ });
        };

        const sendOne = (file, index) => new Promise((resolve) => {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', url + '?name=' + encodeURIComponent(file.name), true);
            xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream');
            xhr.timeout = 120 * 60 * 1000;
            xhr.upload.onprogress = (e) => {
                loaded[index] = e.loaded;
                report(false);
            };
            const finish = (result) => {
                loaded[index] = file.size;
                report(true);
                dotNetRef.invokeMethodAsync('OnFileUploaded', result).catch(() => { /* the circuit went away */ }).finally(resolve);
            };
            xhr.onload = () => {
                if (xhr.status >= 200 && xhr.status < 300) {
                    try {
                        finish(JSON.parse(xhr.responseText));
                        return;
                    } catch { /* fall through */ }
                }
                finish({ photoId: null, fileName: file.name, isVideo: false, problem: 'Upload failed (HTTP ' + xhr.status + ').' });
            };
            xhr.onerror = () => finish({ photoId: null, fileName: file.name, isVideo: false, problem: 'Network error during upload.' });
            xhr.ontimeout = () => finish({ photoId: null, fileName: file.name, isVideo: false, problem: 'The upload timed out.' });
            xhr.send(file);
        });

        const worker = async () => {
            while (next < files.length) {
                const index = next++;
                await sendOne(files[index], index);
            }
        };

        return Promise.all([worker(), worker(), worker()]).then(() => {
            input.value = '';
            return files.length;
        });
    },
};
