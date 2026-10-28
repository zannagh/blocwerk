import { defineConfig } from '@playwright/test';
import { LAUNCH, PROFILES } from './gate/profiles.mjs';

// The offline client's budget gate (plan 2026-10-22, PR 3.8): `npm run gate`. Not part of `npm test` or CI: it
// measures, it does not check behaviour, and its numbers depend on the machine. It publishes Release (the bundle a
// release ships) through the same harness, one test at a time, so no measurement shares the machine with another.
// `BW_GATE_PROFILES=android-mid` narrows it; the report is gate/report.mjs over .out/gate/results.json.
process.env.BW_CONFIGURATION = process.env.BW_CONFIGURATION || 'Release';
const names = (process.env.BW_GATE_PROFILES || Object.keys(PROFILES).join(',')).split(',');

export default defineConfig({
    testDir: './gate',
    globalSetup: './global-setup.mjs',
    workers: 1,
    fullyParallel: false,
    retries: 0,
    timeout: 900000,
    reporter: 'list',
    use: { trace: 'off' },
    projects: names.map(name => ({
        name,
        use: {
            browserName: PROFILES[name].engine,
            gateProfile: name,
            launchOptions: LAUNCH[PROFILES[name].engine]
        }
    }))
});
