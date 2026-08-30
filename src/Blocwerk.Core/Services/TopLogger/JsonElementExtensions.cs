using System.Globalization;
using System.Text.Json;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Tolerant readers over <see cref="JsonElement"/> for defensive mapping of
/// authenticated TopLogger responses whose leaf scalar types are not proven by
/// captured samples. Every helper is null-, missing- and type-mismatch-safe:
/// they never throw and return <c>null</c> (or an empty sequence) when the
/// property is absent, JSON <c>null</c>, or the wrong kind.
/// </summary>
internal static class JsonElementExtensions
{
    /// <summary>
    /// Tries to read a non-null property value from an object element.
    /// </summary>
    public static bool TryProp(this JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty(name, out JsonElement found))
        {
            return false;
        }

        if (found.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        value = found;
        return true;
    }

    /// <summary>
    /// Tries to read a nested object property.
    /// </summary>
    public static bool TryObj(this JsonElement element, string name, out JsonElement value)
    {
        if (element.TryProp(name, out JsonElement found) && found.ValueKind == JsonValueKind.Object)
        {
            value = found;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Enumerates an array property, or an empty sequence when it is missing or
    /// not an array.
    /// </summary>
    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element, string name)
    {
        if (element.TryProp(name, out JsonElement found) && found.ValueKind == JsonValueKind.Array)
        {
            return found.EnumerateArray();
        }

        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// Reads a string property, or <c>null</c> when absent or not a string.
    /// </summary>
    public static string? GetStringOrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Reads a scalar as its raw display text, accepting either a JSON string or
    /// number (e.g. a grade that may arrive as <c>633</c> or <c>"6A"</c>).
    /// </summary>
    public static string? GetRawTextOrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// Reads an integer, accepting a JSON number or a numeric string.
    /// </summary>
    public static long? GetInt64OrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out long number))
                {
                    return number;
                }

                return value.TryGetDouble(out double asDouble) ? (long)asDouble : null;
            case JsonValueKind.String:
                string? text = value.GetString();
                if (long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed))
                {
                    return parsed;
                }

                return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedDouble)
                    ? (long)parsedDouble
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a floating-point number, accepting a JSON number or numeric string.
    /// </summary>
    public static double? GetDoubleOrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out double number) ? number : null;
            case JsonValueKind.String:
                return double.TryParse(
                    value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a boolean, accepting a JSON boolean, <c>"true"/"false"</c> string or
    /// a number (non-zero is <c>true</c>).
    /// </summary>
    public static bool? GetBoolOrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return bool.TryParse(value.GetString(), out bool parsed) ? parsed : null;
            case JsonValueKind.Number:
                return value.TryGetDouble(out double number) ? number != 0 : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a date/time, accepting an ISO-8601 string.
    /// </summary>
    public static DateTimeOffset? GetDateTimeOffsetOrNull(this JsonElement element, string name)
    {
        if (!element.TryProp(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (value.TryGetDateTimeOffset(out DateTimeOffset dto))
        {
            return dto;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
