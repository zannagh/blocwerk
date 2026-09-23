using System.Text.RegularExpressions;

namespace Blocwerk.Core.Compute;

/// <summary>
/// Turns a worker-provided error text into a short reason that is safe to store and show an admin:
/// one line only (a traceback's last line, else the first), file-system paths replaced, control characters
/// removed, at most <see cref="MaxLength"/> characters. The full text belongs in the server log.
/// </summary>
public static partial class ComputeErrorText
{
    public const int MaxLength = 200;

    /// <summary>The sanitized reason, or null when nothing presentable is left.</summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A traceback ends in the exception's own message; anything else leads with its reason.
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var traceback = lines.Any(l => l.StartsWith("Traceback", StringComparison.OrdinalIgnoreCase));
        var line = traceback ? lines[^1] : lines[0];

        line = WindowsPath().Replace(UnixPath().Replace(line, "…"), "…");
        line = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= MaxLength ? line : line[..MaxLength] + "…";
    }

    // Two or more path segments ("/app/x.py", "/tmp/job/p01.jpg"), not a lone "/" or "a/b" ratio.
    [GeneratedRegex(@"(?<![\w.])(?:~|\.{1,2})?(?:/[\w.@+-]+){2,}/?")]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\s""']*")]
    private static partial Regex WindowsPath();
}
