// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>One server-sent event as a browser would split it: its event name, id, data, or a comment.</summary>
internal sealed record SseFrame(string? Event, string? Id, string? Data, string? Comment, string? Retry)
{
    /// <summary>The complete frames of <paramref name="text"/>; a trailing partial frame is left out.</summary>
    public static IReadOnlyList<SseFrame> Parse(string text)
    {
        var blocks = text.Split("\n\n");
        return blocks.Take(blocks.Length - 1).Where(b => b.Length > 0).Select(ParseBlock).ToList();
    }

    private static SseFrame ParseBlock(string block)
    {
        string? name = null, id = null, data = null, comment = null, retry = null;
        foreach (var line in block.Split('\n'))
        {
            if (line.StartsWith(':'))
            {
                comment = line[1..].Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event": name = value; break;
                case "id": id = value; break;
                case "data": data = value; break;
                case "retry": retry = value; break;
            }
        }

        return new SseFrame(name, id, data, comment, retry);
    }
}
