import { defineConfig, devices } from '@playwright/test';

// One worker: the tests share one app, one seeded world and one proxy whose fault rules are global, and each test
// gets its own browser context (its own storage, service worker and cookies). Chromium always; WebKit too, since
// Playwright's WebKit runs on macOS and Linux. `BW_BROWSERS=chromium` narrows it.
const browsers = (process.env.BW_BROWSERS || 'chromium,webkit').split(',');

export default defineConfig({
    testDir: './tests',
    globalSetup: './global-setup.mjs',
    workers: 1,
    fullyParallel: false,
    retries: process.env.CI ? 1 : 0,
    timeout: 60000,
    expect: { timeout: 15000 },
    reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
    use: {
        // Traces are recorded by the context fixture (harness/fixtures.mjs), which builds the contexts itself.
        trace: 'off',
        serviceWorkers: 'allow'
    },
    projects: [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
        { name: 'webkit', use: { ...devices['Desktop Safari'] } }
    ].filter(p => browsers.includes(p.name))
});
