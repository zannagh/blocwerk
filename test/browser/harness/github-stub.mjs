/*
 * Stands in for api.github.com for the app's /whats-new page (Blocwerk:Releases:ApiBaseUrl points here), so the
 * browser tests never reach the real GitHub. Serves one fixed release list for any repository. The notes carry a
 * <script> on purpose: the page must show it as text, never run it.
 */
import http from 'node:http';

export const stubReleases = [
    {
        tag_name: '0.1.1-224', name: '0.1.1-224', prerelease: true, draft: false,
        published_at: '2026-10-24T17:38:54Z', html_url: 'https://github.com/zannagh/blocwerk/releases/tag/0.1.1-224',
        body: '## What\'s Changed\n* fix: a stubbed preview fix by @zannagh\n\n<script>window.__bwReleaseNotesPwned = true</script>\n'
    },
    {
        tag_name: 'v0.1.0', name: 'Blocwerk 0.1.0', prerelease: false, draft: false,
        published_at: '2026-09-14T18:34:47Z', html_url: 'https://github.com/zannagh/blocwerk/releases/tag/v0.1.0',
        body: '## Highlights\n* **Stubbed** stable release notes, see [the docs](https://example.com/docs)\n'
    }
];

export function startGitHubStub() {
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://stub');
        const page = Number(url.searchParams.get('page') || 1);
        if (!/^\/repos\/[^/]+\/[^/]+\/releases$/.test(url.pathname)) {
            res.writeHead(404).end();
            return;
        }
        res.writeHead(200, { 'content-type': 'application/json', etag: '"stub-1"' });
        res.end(JSON.stringify(page === 1 ? stubReleases : []));
    });
    return new Promise((resolve, reject) => {
        server.on('error', reject);
        server.listen(0, '127.0.0.1', () => resolve({
            url: `http://127.0.0.1:${server.address().port}`,
            close: () => new Promise(r => server.close(() => r()))
        }));
    });
}
