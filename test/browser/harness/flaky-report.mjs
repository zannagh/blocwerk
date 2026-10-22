/*
 * Reads Playwright's JSON report and prints a GitHub Actions `::warning::` for every flaky test (failed, then passed
 * on its retry), so a retry never hides one. Exits 0 either way: a flaky test is a warning, not a failure.
 * Usage: node harness/flaky-report.mjs <results.json>
 */
import fs from 'node:fs';

const file = process.argv[2];
if (!file || !fs.existsSync(file)) {
    console.log(`no report at ${file}: nothing to annotate`);
    process.exit(0);
}

const flaky = [];
function walk(suite, titles) {
    for (const spec of suite.specs || []) {
        for (const test of spec.tests || []) {
            if (test.status === 'flaky') {
                flaky.push({ file: spec.file, line: spec.line, title: [...titles, spec.title].join(' › '), project: test.projectName });
            }
        }
    }
    for (const child of suite.suites || []) {
        walk(child, child.title && !child.title.endsWith('.mjs') ? [...titles, child.title] : titles);
    }
}

const report = JSON.parse(fs.readFileSync(file, 'utf8'));
for (const suite of report.suites || []) {
    walk(suite, []);
}
for (const f of flaky) {
    console.log(`::warning file=test/browser/tests/${f.file},line=${f.line},title=Flaky browser test::[${f.project}] ${f.title} passed only on a retry`);
}
console.log(`${flaky.length} flaky test(s)`);
