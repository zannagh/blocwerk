import { startEnvironment } from './harness/environment.mjs';

// Playwright runs the returned function as the global teardown.
export default async function globalSetup() {
    const { stop } = await startEnvironment();
    return stop;
}
