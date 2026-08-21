/*
 * Poster frames for beta videos.
 *
 * WHY IN THE BROWSER: the runtime image (see docker/Dockerfile) has no ffmpeg, and adding one just
 * to grab a single frame would drag a large native dependency into the deploy. The browser already
 * has a video decoder, so the file the user just picked is decoded there and one frame is handed
 * back as a small JPEG; the server stores it verbatim next to the clip.
 *
 * Everything here fails soft: if the browser cannot decode the container (an iPhone .mov that
 * Chrome won't touch, say) it returns null, the upload still goes through, and the tile falls back
 * to a placeholder rather than blocking the user over a thumbnail.
 */
window.blocwerkBetaVideo = {
    /**
     * Decodes the file currently selected in the given <input type="file"> and returns a base64
     * JPEG of a frame near the start, or null.
     */
    async captureThumbnail(inputId, maxEdge) {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) {
            return null;
        }

        const url = URL.createObjectURL(file);
        try {
            return await grabFrame(url, maxEdge || 480);
        } catch (e) {
            return null;
        } finally {
            URL.revokeObjectURL(url);
        }
    },

    /** Plays a clip from the start. Called when the lightbox opens so a tap goes straight to video. */
    play(element) {
        if (!element) {
            return;
        }

        try {
            element.currentTime = 0;
            const started = element.play();
            if (started && typeof started.catch === 'function') {
                // Autoplay may be refused (no user gesture, low power mode). The controls are
                // visible either way, so a refusal is not worth surfacing.
                started.catch(() => { });
            }
        } catch (e) {
            /* same reasoning as above */
        }
    }
};

function grabFrame(url, maxEdge) {
    return new Promise((resolve, reject) => {
        const video = document.createElement('video');
        video.muted = true;
        video.playsInline = true;
        video.preload = 'metadata';
        video.crossOrigin = 'anonymous';

        // A clip that never fires loadeddata (unsupported codec, corrupt file) would otherwise
        // leave the upload waiting forever.
        const timer = setTimeout(() => cleanup(null), 8000);

        function cleanup(result, error) {
            clearTimeout(timer);
            video.removeAttribute('src');
            video.load();
            if (error) {
                reject(error);
            } else {
                resolve(result);
            }
        }

        video.addEventListener('error', () => cleanup(null));

        video.addEventListener('loadeddata', () => {
            // Seek a little way in: the very first frame of a phone recording is often black or
            // still auto-exposing. Stay inside the clip for the ones shorter than that.
            const target = Math.min(0.5, (video.duration || 1) / 4);
            if (Number.isFinite(target) && target > 0 && video.currentTime < target) {
                video.currentTime = target;
            } else {
                draw();
            }
        });

        video.addEventListener('seeked', draw);

        function draw() {
            try {
                const w = video.videoWidth;
                const h = video.videoHeight;
                if (!w || !h) {
                    cleanup(null);
                    return;
                }

                const scale = Math.min(1, maxEdge / Math.max(w, h));
                const canvas = document.createElement('canvas');
                canvas.width = Math.max(1, Math.round(w * scale));
                canvas.height = Math.max(1, Math.round(h * scale));
                canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);

                const dataUrl = canvas.toDataURL('image/jpeg', 0.7);
                const comma = dataUrl.indexOf(',');
                cleanup(comma < 0 ? null : dataUrl.slice(comma + 1));
            } catch (e) {
                // Tainted canvas or a decoder that produced no frame: no thumbnail, no failure.
                cleanup(null);
            }
        }

        video.src = url;
    });
}
