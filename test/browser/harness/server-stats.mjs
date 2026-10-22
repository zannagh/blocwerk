/*
 * Server-side work, read from the app's own Prometheus endpoint (/metrics, forwarded by the proxy): how many full
 * wall loads and boulder-list reads ran, and how many ms the instrumented operations and the WallDetail component's
 * parameter passes took. A test diffs two readings around one navigation.
 */
const sum = (text, pattern) => {
    let total = 0;
    for (const line of text.split('\n')) {
        if (!line.startsWith('#') && pattern.test(line)) {
            total += Number(line.trim().split(/\s+/)[1]) || 0;
        }
    }
    return total;
};

export async function serverStats(page) {
    const text = await page.evaluate(async () => (await fetch('/metrics', { cache: 'no-store' })).text());
    return {
        wallGet: sum(text, /^blocwerk_operation_duration_milliseconds_count\{[^}]*operation="Wall\.Get"/),
        boulderList: sum(text, /^blocwerk_operation_duration_milliseconds_count\{[^}]*operation="BoulderFeedback\.GetBoulderList"/),
        operations: sum(text, /^blocwerk_operation_duration_milliseconds_count\{/),
        operationMs: sum(text, /^blocwerk_operation_duration_milliseconds_sum\{/),
        wallDetailPasses: sum(text, /^aspnetcore_components_update_parameters_duration_seconds_count\{[^}]*Pages\.Walls\.WallDetail"/),
        wallDetailMs: 1000 * sum(text, /^aspnetcore_components_update_parameters_duration_seconds_sum\{[^}]*Pages\.Walls\.WallDetail"/),
        componentMs: 1000 * sum(text, /^aspnetcore_components_update_parameters_duration_seconds_sum\{/),
    };
}

export function statsDelta(before, after) {
    return Object.fromEntries(Object.keys(after).map(k => [k, Math.round((after[k] - before[k]) * 10) / 10]));
}
