// The offline client's bundle budget (plan 2026-10-22, PR 3.8): the Brotli bytes of every file a cold /v/ boot loads,
// summed from a Release publish of Blocwerk.Client, against the gate's threshold (test/browser/gate/profiles.mjs,
// bundleBrotliKiB). Deterministic: it reads files, it measures nothing.
//
//   dotnet publish src/Blocwerk.Client -c Release -o <out>
//   node build/check-client-bundle.mjs <out>/wwwroot [limitKiB]
//
// A boot loads the shell's own files (v/css, v/js), Blocwerk.Shared.Ui's (its CSS and the six cache modules: the whole
// library wwwroot, under v/_content/ in a standalone publish), blazor.webassembly.js, dotnet.js and what dotnet.js's
// boot manifest names: the native runtime and its JS halves, and every eagerly loaded assembly. The rendered shell
// itself (about 2 KiB) is left out.
import fs from 'node:fs';
import path from 'node:path';

const LIMIT_KIB = Number(process.argv[3] || 2560);
const root = path.resolve(process.argv[2] || '');
const framework = path.join(root, 'v', '_framework');
if (!fs.existsSync(framework)) {
    console.error(`check-client-bundle: no v/_framework under ${root}; pass the client publish's wwwroot.`);
    process.exit(2);
}

const filesUnder = dir => (fs.existsSync(dir) ? fs.readdirSync(dir, { recursive: true }) : [])
    .map(f => path.join(dir, f)).filter(f => fs.statSync(f).isFile() && !/\.(br|gz)$/.test(f));
const inFramework = re => fs.readdirSync(framework).filter(f => re.test(f)).map(f => path.join(framework, f));

const loader = inFramework(/^blazor\.webassembly(\.[a-z0-9]+)?\.js$/);
const dotnet = inFramework(/^dotnet(\.[a-z0-9]+)?\.js$/);
if (loader.length !== 1 || dotnet.length !== 1) {
    console.error(`check-client-bundle: expected one loader and one dotnet.js, found ${loader.length} and ${dotnet.length}.`);
    process.exit(2);
}

// The boot manifest dotnet.js carries between /*json-start*/ and /*json-end*/.
const source = fs.readFileSync(dotnet[0], 'utf8');
const json = source.slice(source.indexOf('/*json-start*/') + 14, source.indexOf('/*json-end*/'));
const resources = JSON.parse(json).resources;
const booted = ['jsModuleNative', 'jsModuleRuntime', 'wasmNative', 'coreAssembly', 'assembly']
    .flatMap(kind => resources[kind] || []).map(r => path.join(framework, r.name));

const files = [
    ...filesUnder(path.join(root, 'v', 'css')),
    ...filesUnder(path.join(root, 'v', 'js')),
    ...filesUnder(path.join(root, 'v', '_content', 'Blocwerk.Shared.Ui')),
    ...loader, ...dotnet, ...booted,
];
const rows = files.map(f => {
    if (!fs.existsSync(`${f}.br`)) {
        console.error(`check-client-bundle: ${path.relative(root, f)} has no .br beside it.`);
        process.exit(2);
    }
    return { file: path.relative(root, f), bytes: fs.statSync(`${f}.br`).size };
});

const totalKiB = Math.round(rows.reduce((t, r) => t + r.bytes, 0) / 1024);
for (const r of rows.sort((a, b) => b.bytes - a.bytes).slice(0, 5)) {
    console.log(`  ${String(Math.round(r.bytes / 1024)).padStart(5)} KiB  ${r.file}`);
}
console.log(`Offline client bundle: ${totalKiB} KiB Brotli in ${rows.length} files (limit ${LIMIT_KIB} KiB).`);
if (totalKiB > LIMIT_KIB) {
    console.error(`check-client-bundle: over the budget by ${totalKiB - LIMIT_KIB} KiB.`);
    process.exit(1);
}
