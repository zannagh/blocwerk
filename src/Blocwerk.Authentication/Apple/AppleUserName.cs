// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// Reads the name out of the <c>user</c> field Apple posts on the first authorization only:
/// <c>{"name":{"firstName":"…","lastName":"…"},"email":"…"}</c>. The e-mail is ignored on purpose.
/// </summary>
public static class AppleUserName
{
    /// <summary>The longest name kept (the display name column holds 256).</summary>
    public const int MaxLength = 100;

    private const int MaxFieldLength = 4096;

    /// <summary>The trimmed "first last" name, or null when absent, malformed, too long or carrying invisible characters.</summary>
    public static string? Parse(string? userField)
    {
        if (string.IsNullOrWhiteSpace(userField) || userField.Length > MaxFieldLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(userField);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var full = $"{Part(name, "firstName")} {Part(name, "lastName")}".Trim();
            return full.Length is 0 or > MaxLength || DisplayNameRules.HasInvisibleCharacters(full) ? null : full;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Part(JsonElement name, string property) =>
        name.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
