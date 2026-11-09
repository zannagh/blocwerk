// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition;

/// <summary>
/// What an instance runner key (runner protocol 1.2, §1.5) must be: <see cref="RecognitionQueueOptions.MinInstanceKeyLength"/>
/// to <see cref="RecognitionQueueOptions.MaxInstanceKeyLength"/> characters, not a placeholder copied from an example
/// (<c>change-me…</c>) and not low-entropy (fewer than <see cref="MinDistinctCharacters"/> distinct characters; a random
/// hex key of 32 or more characters has nearly all 16).
/// </summary>
public static class InstanceKeyPolicy
{
    public const string PlaceholderPrefix = "change-me";

    public const int MinDistinctCharacters = 10;

    /// <summary>Why <paramref name="key"/> cannot be the instance key (never the key itself), or null when it can.</summary>
    public static string? Refusal(string key)
    {
        if (key.Length is < RecognitionQueueOptions.MinInstanceKeyLength or > RecognitionQueueOptions.MaxInstanceKeyLength)
        {
            return $"it has {key.Length} characters, not {RecognitionQueueOptions.MinInstanceKeyLength} to {RecognitionQueueOptions.MaxInstanceKeyLength}";
        }

        if (key.StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "it is the example's placeholder";
        }

        return key.Distinct().Count() < MinDistinctCharacters
            ? $"it has fewer than {MinDistinctCharacters} distinct characters"
            : null;
    }
}
