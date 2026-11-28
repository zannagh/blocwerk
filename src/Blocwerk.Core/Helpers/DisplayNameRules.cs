// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// What a custom display name may not contain: the invisible characters that let a name look like another member's or
/// reorder the text around it. Bidirectional embeddings, overrides and isolates (U+202A–U+202E, U+2066–U+2069), the
/// direction marks (U+200E, U+200F, U+061C), and zero-width characters (U+200B, U+200C, U+2060–U+2064, U+FEFF, U+180E).
/// </summary>
/// <remarks>
/// The zero-width joiner (U+200D) is allowed between two emoji, where it builds one emoji out of several (the woman
/// climbing, a family, a skin-toned profession), and refused anywhere else.
/// </remarks>
public static class DisplayNameRules
{
    /// <summary>The refusal shown to the person.</summary>
    public const string InvisibleCharacterMessage = "A display name cannot contain invisible or text-direction characters.";

    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>True when <paramref name="name"/> holds a character a display name may not contain.</summary>
    public static bool HasInvisibleCharacters(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var runes = name.EnumerateRunes().ToList();
        for (var i = 0; i < runes.Count; i++)
        {
            var value = runes[i].Value;
            if (value == ZeroWidthJoiner ? !JoinsEmoji(runes, i) : IsInvisible(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvisible(int value) => value switch
    {
        >= 0x200B and <= 0x200F => true,
        >= 0x202A and <= 0x202E => true,
        >= 0x2060 and <= 0x2064 => true,
        >= 0x2066 and <= 0x2069 => true,
        0x061C or 0x180E or 0xFEFF => true,
        _ => false,
    };

    // An emoji sequence: an emoji (or its variation selector or skin tone, which close an emoji) before, an emoji after.
    private static bool JoinsEmoji(List<Rune> runes, int joiner) =>
        joiner > 0
        && joiner < runes.Count - 1
        && (IsEmoji(runes[joiner - 1]) || IsEmojiModifier(runes[joiner - 1]))
        && IsEmoji(runes[joiner + 1]);

    private static bool IsEmoji(Rune rune) =>
        rune.Value >= 0x1F000 || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;

    private static bool IsEmojiModifier(Rune rune) =>
        rune.Value == 0xFE0F || rune.Value is >= 0x1F3FB and <= 0x1F3FF;
}
