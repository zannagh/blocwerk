using System.Text.Json;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Pure, defensive mapper from a raw <c>climbLogs.data</c> entry to a
/// <see cref="TopLoggerTick"/>. Tolerates missing / null / mismatched fields.
/// </summary>
internal static class TopLoggerTickMapping
{
    public static TopLoggerTick MapTick(JsonElement log)
    {
        log.TryObj("climb", out JsonElement climb);

        bool hasClimb = climb.ValueKind == JsonValueKind.Object;
        string? climbName = null;
        string? rawGrade = null;
        string? climbClimbType = null;
        string? gymName = null;
        string? gymSlug = null;
        if (hasClimb)
        {
            climbName = climb.GetStringOrNull("name");
            rawGrade = climb.GetRawTextOrNull("grade");
            climbClimbType = climb.GetStringOrNull("climbType");
            if (climb.TryObj("gym", out JsonElement gym))
            {
                gymName = gym.GetStringOrNull("name");
                gymSlug = gym.GetStringOrNull("nameSlug");
            }
        }

        return new TopLoggerTick(
            log.GetStringOrNull("id") ?? string.Empty,
            log.GetStringOrNull("climbId"),
            climbName,
            log.GetStringOrNull("climbType") ?? climbClimbType,
            log.GetStringOrNull("gymId"),
            gymName,
            gymSlug,
            log.GetDateTimeOffsetOrNull("climbedAtDate"),
            log.GetStringOrNull("tickType"),
            (int)(log.GetInt64OrNull("tryIndex") ?? 0),
            log.GetBoolOrNull("ticked") ?? false,
            log.GetBoolOrNull("topped"),
            log.GetDoubleOrNull("points"),
            rawGrade,
            GradeFormatter.ToFontGrade(rawGrade));
    }
}
