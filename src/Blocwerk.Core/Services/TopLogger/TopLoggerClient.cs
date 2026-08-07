using System.Net;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Default <see cref="ITopLoggerClient"/>. Implements the documented legacy REST API
/// (api.toplogger.nu/v1) fully and detects when a login isn't valid there (e.g. the account lives on
/// the newer app.toplogger.com platform), reporting a clear message. The modern GraphQL path is a
/// follow-up once we have a real response to model against.
///
/// The API is unofficial and undocumented; parsing is deliberately defensive and the first raw ascent
/// is logged at Debug (ascents carry no secrets) so the field mapping can be confirmed against live data.
/// </summary>
public sealed class TopLoggerClient : ITopLoggerClient
{
    private const string LegacyBase = "https://api.toplogger.nu/v1";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<TopLoggerClient> logger;

    public TopLoggerClient(IHttpClientFactory httpClientFactory, ILogger<TopLoggerClient> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    public async Task<TopLoggerAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("toplogger");
        var payload = JsonSerializer.Serialize(new { user = new { email, password } });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(
                $"{LegacyBase}/users/sign_in",
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "TopLogger sign-in request failed to reach the API.");
            return new TopLoggerAuthResult(false, null, null, TopLoggerBackend.Unknown, "Could not reach TopLogger. Try again later.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new TopLoggerAuthResult(
                false, null, null, TopLoggerBackend.Unknown,
                $"TopLogger sign-in failed ({(int)response.StatusCode}). Check your email/password. " +
                "If your gym uses the newer app.toplogger.com platform, it isn't supported yet.");
        }

        string? token;
        string? uid;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            token = FirstString(root, "authentication_token", "token", "auth_token");
            uid = FirstString(root, "uid", "id");
        }
        catch (JsonException)
        {
            return new TopLoggerAuthResult(false, null, null, TopLoggerBackend.Legacy, "TopLogger returned an unexpected sign-in response.");
        }

        if (string.IsNullOrEmpty(token) && response.Headers.TryGetValues("X-USER-TOKEN", out var headerToken))
        {
            token = headerToken.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(token))
        {
            return new TopLoggerAuthResult(false, null, null, TopLoggerBackend.Legacy, "TopLogger sign-in returned no token; the API shape may have changed.");
        }

        return new TopLoggerAuthResult(true, token, uid, TopLoggerBackend.Legacy, null);
    }

    public async Task<IReadOnlyList<TopLoggerAscentDto>> GetAscentsAsync(
        TopLoggerCredentials credentials, DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        if (credentials.Backend != TopLoggerBackend.Legacy)
        {
            throw new NotSupportedException("Only the legacy TopLogger backend is supported in this version.");
        }

        if (string.IsNullOrEmpty(credentials.UserUid))
        {
            return [];
        }

        object uidValue = long.TryParse(credentials.UserUid, out var numericUid) ? numericUid : credentials.UserUid;
        var jsonParams = JsonSerializer.Serialize(new
        {
            filters = new { used = true, user = new { uid = uidValue } },
            includes = new[] { "climb" },
        });

        var url = $"{LegacyBase}/ascends.json?json_params={Uri.EscapeDataString(jsonParams)}&serialize_checks=true";
        var client = httpClientFactory.CreateClient("toplogger");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-USER-EMAIL", credentials.Email);
        request.Headers.Add("X-USER-TOKEN", credentials.Token);

        var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new TopLoggerAuthException("TopLogger rejected the stored token — reconnect needed.");
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseAscents(body, since);
    }

    private IReadOnlyList<TopLoggerAscentDto> ParseAscents(string body, DateTimeOffset? since)
    {
        var results = new List<TopLoggerAscentDto>();
        using var doc = JsonDocument.Parse(body);

        // The endpoint returns an array (older shape) or an object with an "ascends"/"data" array.
        var array = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : FirstArray(doc.RootElement, "ascends", "ascents", "data");

        if (array is not { ValueKind: JsonValueKind.Array })
        {
            logger.LogWarning("TopLogger ascends response was not an array; shape may have changed.");
            return results;
        }

        var loggedFirst = false;
        foreach (var element in array.Value.EnumerateArray())
        {
            if (!loggedFirst)
            {
                // Logged at Information (one line per sync) while we validate the field/grade mapping
                // against live data. Lower to Debug once the mapping is confirmed.
                logger.LogInformation("TopLogger first ascent payload (for field mapping): {Payload}", element.GetRawText());
                loggedFirst = true;
            }

            var type = ChecksToType(element);
            if (type is null)
            {
                continue; // not a send/flash (a project or attempt) — skip
            }

            var id = FirstString(element, "id", "uid");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var loggedAt = FirstDate(element, "date_logged", "logged_at", "created_at");
            if (loggedAt is null || (since is not null && loggedAt < since))
            {
                continue;
            }

            string? climbName = null;
            string? gymName = null;
            string? gradeRaw = null;
            string? gradeSystem = null;
            if (FirstObject(element, "climb") is { } climb)
            {
                climbName = FirstString(climb, "name");
                gradeRaw = FirstString(climb, "grade");
                gradeSystem = FirstString(climb, "grade_system");
                if (FirstObject(climb, "gym") is { } gym)
                {
                    gymName = FirstString(gym, "name");
                }
            }

            gradeRaw ??= FirstString(element, "grade");

            results.Add(new TopLoggerAscentDto(id, climbName, gymName, gradeRaw, gradeSystem, type.Value, loggedAt.Value));
        }

        return results;
    }

    private static AttemptType? ChecksToType(JsonElement ascent)
    {
        if (ascent.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Number)
        {
            return checks.GetInt32() switch
            {
                2 => AttemptType.Flash,
                1 => AttemptType.Send,
                _ => null,
            };
        }

        return null;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        return value.GetString();
                    case JsonValueKind.Number:
                        return value.GetRawText();
                }
            }
        }

        return null;
    }

    private static JsonElement? FirstObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static JsonElement? FirstArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstDate(JsonElement element, params string[] names)
    {
        var raw = FirstString(element, names);
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }
}
