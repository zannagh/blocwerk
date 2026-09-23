// <copyright file="CaptureToolProcess.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel;
using System.Diagnostics;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Runs ffmpeg/ffprobe for the capture video: arguments as a list (never a shell string), stdout fed
/// line by line to a callback, a hard timeout that kills the whole process tree, and a failure that
/// carries the tail of stderr.
/// </summary>
internal static class CaptureToolProcess
{
    /// <summary>Runs the tool to completion; returns its stdout. Throws <see cref="InvalidDataException"/> on failure.</summary>
    public static async Task<string> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(fileName)} is not installed on this server.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = ReadLinesAsync(process.StandardOutput, onLine, timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await stdout;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(fileName)} failed ({process.ExitCode}): {Tail(await stderr)}");
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidDataException($"{Path.GetFileName(fileName)} took longer than {timeout.TotalMinutes:0} minutes.");
        }
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        var all = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            all.AppendLine(line);
            onLine?.Invoke(line);
        }

        return all.ToString();
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
