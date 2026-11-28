// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>
/// What a custom display name may not contain: the invisible characters that let a name look like another member's or
/// reorder the text around it. Bidirectional embeddings, overrides and isolates (U+202A–U+202E, U+2066–U+2069), the
/// direction marks (U+200E, U+200F, U+061C), and zero-width characters (U+200B–U+200D, U+2060–U+2064, U+FEFF, U+180E).
/// </summary>
/// <remarks>
/// The zero-width joiner (U+200D) is refused too, which also refuses emoji built from several joined emoji (a family, a
/// profession with a skin tone). A single emoji is fine.
/// </remarks>
public static class DisplayNameRules
{
    /// <summary>The refusal shown to the person.</summary>
    public const string InvisibleCharacterMessage = "A display name cannot contain invisible or text-direction characters.";

    /// <summary>True when <paramref name="name"/> holds a character a display name may not contain.</summary>
    public static bool HasInvisibleCharacters(string? name) =>
        name is not null && name.Any(IsInvisible);

    private static bool IsInvisible(char c) => c switch
    {
        >= '​' and <= '‏' => true,
        >= '‪' and <= '‮' => true,
        >= '⁠' and <= '⁤' => true,
        >= '⁦' and <= '⁩' => true,
        '؜' or '᠎' or '﻿' => true,
        _ => false,
    };
}
