using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Temperature series of a wall, written by the sensor bound to that wall and read back by
/// anything holding the same key. Authentication is API key only — a browser cookie must never
/// reach these routes, which is why the scheme is pinned explicitly.
/// </summary>
[ApiController]
[Route("api/walls/{wallId:guid}/temperature")]
[Authorize(Policy = BlocwerkPolicies.WallApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class WallTemperatureController : WallScopedApiController
{
    /// <summary>Physically plausible band for a climbing wall; anything outside is a broken sensor.</summary>
    private const double MinCelsius = -80d;
    private const double MaxCelsius = 80d;

    /// <summary>Samples returned when the caller does not say how many it wants.</summary>
    private const int DefaultSamples = 2000;

    /// <summary>Longest window a single read may span, to keep one call from pulling a whole year.</summary>
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);

    private static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    private readonly IWallTemperatureService temperatureService;

    public WallTemperatureController(IWallTemperatureService temperatureService)
    {
        this.temperatureService = temperatureService;
    }

    /// <summary>
    /// Records one sample. The body is read as a raw <see cref="JsonElement"/> because two
    /// different clients post here and neither may be broken by the other:
    /// <list type="bullet">
    /// <item>the Raspberry Pi in the gym posts a BARE JSON number (<c>24.3</c>) — its firmware is
    /// fixed and cannot be changed;</item>
    /// <item>richer clients post an object (<c>{ "temperatureCelsius": 24.3, "recordedAt": "..." }</c>),
    /// where <c>recordedAt</c> is optional and, when present, is stored as the sample's timestamp
    /// instead of the server clock.</item>
    /// </list>
    /// One route and one action handle both, because a second route would mean a second URL to
    /// document, and a DTO would reject the bare number outright.
    /// The device posts roughly once a second and retries on timeout, so duplicates are expected
    /// and deliberately stored as-is: de-duplicating would silently drop genuine samples that
    /// happen to repeat a value.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Record(
        Guid wallId,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        if (!TryReadTemperature(body, out var temperature, out var error))
        {
            return BadRequest(new ApiErrorResponse(error));
        }

        if (!TryReadRecordedAt(body, out var recordedAt, out error))
        {
            return BadRequest(new ApiErrorResponse(error));
        }

        try
        {
            await temperatureService.RecordReadingAsync(wallId, temperature, recordedAt, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // A supplied timestamp the service considers impossible: a broken client clock.
            return BadRequest(new ApiErrorResponse($"'recordedAt' is not a plausible timestamp. {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            // "Wall not found" — the key outlived the wall it was issued for.
            return NotFound(new ApiErrorResponse(ex.Message));
        }

        return NoContent();
    }

    /// <summary>
    /// Readings between <c>from</c> (inclusive) and <c>to</c> (exclusive), oldest first. Both are
    /// optional and default to the last 24 hours; a window longer than 90 days is clamped to the
    /// most recent 90 days rather than rejected, so a naive "give me everything" still answers.
    /// </summary>
    /// <remarks>
    /// The window alone is no protection — the sensor posts about once a second, so 90 days is
    /// millions of rows. <c>maxSamples</c> caps how many are read and serialised: it defaults to
    /// <see cref="DefaultSamples"/> and may not exceed <see cref="WallTemperatureService.MaxReadings"/>,
    /// and a request above that is rejected rather than quietly clamped. When the window held more
    /// samples than the cap, the MOST RECENT <c>maxSamples</c> are returned and the response
    /// carries <c>X-Blocwerk-Truncated: true</c>, so nothing is dropped behind the caller's back.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetReadings(
        Guid wallId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? maxSamples,
        CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end - DefaultRange;
        if (start >= end)
        {
            return BadRequest(new ApiErrorResponse("'from' must be earlier than 'to'."));
        }

        if (end - start > MaxRange)
        {
            start = end - MaxRange;
        }

        var limit = maxSamples ?? DefaultSamples;
        if (limit < 1 || limit > WallTemperatureService.MaxReadings)
        {
            return BadRequest(new ApiErrorResponse(
                $"'maxSamples' must be between 1 and {WallTemperatureService.MaxReadings}."));
        }

        var page = await temperatureService.GetReadingsAsync(wallId, start, end, limit, cancellationToken);
        if (page.Truncated)
        {
            Response.Headers["X-Blocwerk-Truncated"] = "true";
        }

        return Ok(page.Readings
            .Select(r => new TemperatureReadingResponse(r.RecordedAt, r.TemperatureCelsius))
            .ToList());
    }

    /// <summary>The most recent sample, or 404 when the wall has never reported one.</summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(Guid wallId, CancellationToken cancellationToken)
    {
        var guard = GuardWall(wallId);
        if (guard is not null)
        {
            return guard;
        }

        var reading = await temperatureService.GetLatestReadingAsync(wallId, cancellationToken);
        if (reading is null)
        {
            return NotFound(new ApiErrorResponse("This wall has no temperature readings."));
        }

        return Ok(new TemperatureReadingResponse(reading.RecordedAt, reading.TemperatureCelsius));
    }

    /// <summary>
    /// Pulls the temperature out of either accepted body shape and rejects values a working
    /// sensor cannot produce. NaN and the infinities have to be caught explicitly: they survive
    /// JSON round-trips through some clients and would otherwise poison every average built on
    /// the series.
    /// </summary>
    private static bool TryReadTemperature(JsonElement body, out double temperature, out string error)
    {
        temperature = 0d;
        error = string.Empty;

        JsonElement value;
        switch (body.ValueKind)
        {
            case JsonValueKind.Number:
                value = body;
                break;
            case JsonValueKind.Object when TryGetProperty(body, "temperatureCelsius", out var property):
                value = property;
                break;
            case JsonValueKind.Object:
                error = "The body must contain a 'temperatureCelsius' number.";
                return false;
            default:
                error = "The body must be a JSON number or an object with 'temperatureCelsius'.";
                return false;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out temperature))
        {
            error = "'temperatureCelsius' must be a JSON number.";
            return false;
        }

        if (double.IsNaN(temperature) || double.IsInfinity(temperature))
        {
            error = "The temperature must be a finite number.";
            return false;
        }

        if (temperature is < MinCelsius or > MaxCelsius)
        {
            error = $"The temperature must be between {MinCelsius} and {MaxCelsius} °C.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the optional <c>recordedAt</c> field. Absent (and every bare-number body) means "stamp
    /// it server-side"; a supplied value is passed on to be stored, because accepting the field and
    /// then storing a different timestamp would be a lie the caller cannot see. Plausibility is the
    /// service's call, not this method's.
    /// </summary>
    private static bool TryReadRecordedAt(JsonElement body, out DateTimeOffset? recordedAt, out string error)
    {
        recordedAt = null;
        error = string.Empty;
        if (body.ValueKind != JsonValueKind.Object
            || !TryGetProperty(body, "recordedAt", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed))
        {
            error = "'recordedAt' must be an ISO-8601 timestamp string.";
            return false;
        }

        recordedAt = parsed;
        return true;
    }

    /// <summary>
    /// Finds a property case-insensitively, so camelCase and PascalCase clients both work.
    /// </summary>
    private static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (property.NameEquals(name)
                || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
