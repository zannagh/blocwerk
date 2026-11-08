// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Reads the instance key from <c>RECOGNITION__INSTANCEKEYFILE</c>. The app writes that file at its first start, so until it
/// exists (or while it cannot be read) the worker waits and retries, logged, instead of exiting into a crash loop.
/// </summary>
public static class KeyFileWait
{
    /// <summary>Every this many attempts the wait is logged again.</summary>
    private const int LogEvery = 12;

    public static async Task<string> ReadAsync(string path, WorkerOptions options, WorkerHealth health, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            health.MarkAlive();
            string? why;
            try
            {
                if (File.Exists(path) && (await File.ReadAllTextAsync(path, ct)).Trim() is { Length: > 0 } key)
                {
                    logger.LogInformation("Instance key read from {Path}", path);
                    return key;
                }

                why = "it does not exist yet or is empty";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why = ex.Message;
            }

            if (attempt % LogEvery == 0)
            {
                logger.LogWarning("Waiting for the instance key file {Path} ({Why}); the app writes it at its first start", path, why);
            }

            await Task.Delay(options.RetryDelay, ct);
        }
    }
}
