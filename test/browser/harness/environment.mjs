/*
 * Brings up the world the browser tests run against, and tears it down again:
 *
 *   1. a throwaway Postgres 17 container on a random local port (or BW_BROWSER_PG, a server you provide);
 *   2. `dotnet publish` of Blocwerk.Web into .out/web, so the app runs from a build output and never reads wwwroot
 *      from the source tree while it runs;
 *   3. the seeder (test/browser/seed), which migrates the database and writes the fixed world to .out/seed.json;
 *   4. the published app in Development (for /dev/login), with a scrubbed environment and its own HOME, so it never sees
 *      the developer's secrets, data-protection keys or appsettings;
 *   5. the test proxy (proxy.mjs) in front of it. Tests talk to the proxy only.
 *
 * Nothing here reads configuration from anywhere but this file. The publish refuses to run when the source tree has
 * an appsettings.json, because the publish would copy it into the app's content root.
 */
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { startProxy } from './proxy.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const harnessRoot = path.resolve(here, '..');
export const outDir = path.join(harnessRoot, '.out');
export const stateFile = path.join(outDir, 'state.json');
const repoRoot = path.resolve(harnessRoot, '..', '..');
const webProject = path.join(repoRoot, 'src', 'Blocwerk.Web');
const seedProject = path.join(harnessRoot, 'seed');
const STOP_GRACE_MS = 5000;

function run(cmd, args, opts = {}) {
    try {
        return execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...opts }).trim();
    } catch (e) {
        // The command's own output, so a failed publish or seed says why (in CI there is nothing else to look at):
        // its error lines first (a build's warnings would bury them), else its last lines.
        const lines = `${e.stdout || ''}\n${e.stderr || ''}`.split('\n').filter(l => l.trim());
        const errors = lines.filter(l => /\berror\b/i.test(l) && !/\bwarning\b/i.test(l));
        const shown = (errors.length ? errors : lines).slice(-40).join('\n');
        throw new Error(`${cmd} ${args.join(' ')} failed (exit ${e.status ?? e.signal}):\n${shown}`);
    }
}

function freePort() {
    return new Promise((resolve, reject) => {
        const server = net.createServer();
        server.listen(0, '127.0.0.1', () => {
            const { port } = server.address();
            server.close(() => resolve(port));
        });
        server.on('error', reject);
    });
}

async function until(what, probe, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    let last;
    while (Date.now() < deadline) {
        try {
            if (await probe()) {
                return;
            }
        } catch (e) {
            last = e;
        }
        await new Promise(r => setTimeout(r, 250));
    }
    throw new Error(`timed out waiting for ${what}${last ? `: ${last.message}` : ''}`);
}

// BW_BROWSER_PG: a server you provide (CI's service). Only a local one, and only a `bw_browser_*` database, which is
// dropped and created again so every run starts from the same seed. BW_BROWSER_PG_SERVICE names one more host to
// accept: CI runs in a container, where the Postgres service is reached by its service name, not localhost.
async function providedPostgres(value) {
    const url = new URL(value);
    const pg = {
        host: url.hostname, port: Number(url.port || 5432), user: decodeURIComponent(url.username),
        password: decodeURIComponent(url.password), database: url.pathname.slice(1), stop: () => undefined
    };
    const allowed = ['localhost', '127.0.0.1', '[::1]', process.env.BW_BROWSER_PG_SERVICE].filter(Boolean);
    if (!allowed.includes(pg.host)) {
        throw new Error(`BW_BROWSER_PG must point at localhost or 127.0.0.1, not ${pg.host}: the harness drops its database`);
    }
    if (!/^bw_browser_[a-z0-9_]+$/.test(pg.database)) {
        throw new Error(`BW_BROWSER_PG's database must be named bw_browser_*, not '${pg.database}'`);
    }
    const admin = new URL(value);
    admin.pathname = '/postgres';
    await resetDatabase(pg, admin);
    return pg;
}

// Through the seeder's own Npgsql, so the harness needs no Postgres client of its own.
async function resetDatabase(pg, admin) {
    const connection = `Host=${pg.host};Port=${pg.port};Database=${admin.pathname.slice(1)};Username=${pg.user};Password=${pg.password}`;
    run('dotnet', ['run', '--project', seedProject, '-c', 'Debug', '--', '--reset', connection, pg.database], { cwd: repoRoot });
}

async function startPostgres() {
    if (process.env.BW_BROWSER_PG) {
        return providedPostgres(process.env.BW_BROWSER_PG);
    }
    const id = randomBytes(4).toString('hex');
    const name = `bw-browser-pg-${id}`;
    const password = randomBytes(12).toString('hex');
    const database = `bw_browser_${id}`;
    run('docker', ['run', '-d', '--rm', '--name', name, '-e', `POSTGRES_PASSWORD=${password}`, '-e', `POSTGRES_DB=${database}`,
        '-p', '127.0.0.1::5432', 'postgres:17']);
    const stop = () => { try { run('docker', ['rm', '-f', name]); } catch (e) { /* already gone */ } };
    try {
        const port = Number(run('docker', ['port', name, '5432/tcp']).split('\n')[0].split(':').pop());
        await until('postgres', () => run('docker', ['exec', name, 'pg_isready', '-U', 'postgres', '-d', database]).includes('accepting'), 60000);
        return { host: '127.0.0.1', port, user: 'postgres', password, database, stop };
    } catch (e) {
        stop();
        throw e;
    }
}

function publishApp() {
    for (const file of ['appsettings.json', 'appsettings.Development.json']) {
        if (fs.existsSync(path.join(webProject, file))) {
            throw new Error(`${file} exists in ${webProject}: the publish would copy it into the app. Run the harness from a `
                + 'worktree without it (it is gitignored, so a fresh worktree has none).');
        }
    }
    const webOut = path.join(outDir, 'web');
    fs.rmSync(webOut, { recursive: true, force: true });
    run('dotnet', ['publish', webProject, '-c', 'Debug', '-o', webOut, '--nologo', '-v', 'q'], { cwd: repoRoot });
    return webOut;
}

function seed(pg, storage) {
    const connection = `Host=${pg.host};Port=${pg.port};Database=${pg.database};Username=${pg.user};Password=${pg.password}`;
    const output = path.join(outDir, 'seed.json');
    run('dotnet', ['run', '--project', seedProject, '-c', 'Debug', '--', connection, storage, output], { cwd: repoRoot });
    return JSON.parse(fs.readFileSync(output, 'utf8'));
}

// Only what the runtime needs: no Blocwerk/SMTP/VAPID/OTEL/BLOCWERK_DEV_USER variable of the developer's shell leaks in.
function appEnvironment(pg, port, runDir, storage) {
    const keep = ['PATH', 'DOTNET_ROOT', 'TMPDIR', 'LANG'];
    const env = Object.fromEntries(keep.filter(k => process.env[k]).map(k => [k, process.env[k]]));
    return {
        ...env,
        HOME: path.join(runDir, 'home'),
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
        DOTNET_NOLOGO: '1',
        Blocwerk__Postgres__Host: pg.host,
        Blocwerk__Postgres__Port: String(pg.port),
        Blocwerk__Postgres__Database: pg.database,
        Blocwerk__Postgres__Username: pg.user,
        Blocwerk__Postgres__Password: pg.password,
        Blocwerk__WallImage__StoragePath: storage,
        Blocwerk__BetaVideo__StoragePath: path.join(runDir, 'videos'),
        Blocwerk__GitHubOAuth__Enabled: 'false',
        Blocwerk__Server__Url: `http://127.0.0.1:${port}`
    };
}

// Registers its stop in `stops` before it waits, so an app that never answers is still killed.
async function startApp(webOut, pg, runDir, storage, stops) {
    const port = await freePort();
    fs.mkdirSync(path.join(runDir, 'home'), { recursive: true });
    const log = fs.openSync(path.join(outDir, 'app.log'), 'w');
    const child = spawn('dotnet', [path.join(webOut, 'Blocwerk.Web.dll')], {
        cwd: webOut, env: appEnvironment(pg, port, runDir, storage), stdio: ['ignore', log, log]
    });
    let exited = null;
    const gone = new Promise(resolve => child.on('exit', code => {
        exited = code ?? 'signal';
        resolve();
    }));
    // SIGTERM, then SIGKILL after STOP_GRACE_MS; resolves once the process is gone, so its files can be removed.
    const stop = async () => {
        if (exited !== null) {
            return;
        }
        child.kill('SIGTERM');
        const killer = setTimeout(() => child.kill('SIGKILL'), STOP_GRACE_MS);
        await gone;
        clearTimeout(killer);
    };
    stops.push(stop);
    await until('the app on /alive', async () => {
        if (exited !== null) {
            throw new Error(`app exited with ${exited}; see ${path.join(outDir, 'app.log')}`);
        }
        const response = await fetch(`http://127.0.0.1:${port}/alive`);
        return response.ok;
    }, 120000);
    return { port };
}

export async function startEnvironment() {
    fs.mkdirSync(outDir, { recursive: true });
    const runDir = fs.mkdtempSync(path.join(outDir, 'run-'));
    const storage = path.join(runDir, 'wall-images');
    const stops = [];
    const stop = async () => {
        for (const s of stops.reverse()) {
            await s();
        }
        fs.rmSync(runDir, { recursive: true, force: true });
    };
    try {
        const pg = await startPostgres();
        stops.push(pg.stop);
        const webOut = publishApp();
        const world = seed(pg, storage);
        const app = await startApp(webOut, pg, runDir, storage, stops);
        const proxy = await startProxy(app.port);
        stops.push(proxy.close);
        const state = { baseURL: `http://127.0.0.1:${proxy.port}`, appPort: app.port, world };
        fs.writeFileSync(stateFile, JSON.stringify(state, null, 2));
        return { state, stop };
    } catch (e) {
        await stop();
        throw e;
    }
}
