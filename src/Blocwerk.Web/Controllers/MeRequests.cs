using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST /api/v1/me/sessions</c>.</summary>
public sealed class StartSessionRequest
{
    public Guid WallId { get; set; }
}

/// <summary>Body of <c>POST /api/v1/me/attempts</c>.</summary>
public sealed class LogAttemptApiRequest
{
    public Guid BoulderId { get; set; }

    /// <summary>Accepts the name ("Attempt"/"Send"/"Flash") as well as the numeric value.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AttemptType>))]
    public AttemptType Type { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Stable de-duplication key. Reusing it on a retry returns the attempt already stored
    /// instead of logging a second one, which makes a flaky mobile connection safe to retry.
    /// </summary>
    public Guid? ClientRequestId { get; set; }

    /// <summary>Backdates the attempt; omitted means "now".</summary>
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>Body of <c>POST /api/v1/me/training/hangboard</c>.</summary>
public sealed class SaveHangboardRequest
{
    public int EdgeSizeMm { get; set; }

    public double AdditionalWeightKg { get; set; }

    /// <summary>Hang duration in seconds; the domain model stores a <see cref="TimeSpan"/>.</summary>
    public double DurationSeconds { get; set; }

    public int Sets { get; set; } = 1;

    public string? Notes { get; set; }
}

/// <summary>Body of <c>POST /api/v1/me/training/pullups</c>.</summary>
public sealed class SavePullupRequest
{
    public int Repetitions { get; set; }

    public double AdditionalWeightKg { get; set; }

    public int Sets { get; set; } = 1;

    public string? Notes { get; set; }
}
