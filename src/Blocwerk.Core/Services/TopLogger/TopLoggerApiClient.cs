using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Default <see cref="ITopLoggerApiClient"/>. Resolves the TopLogger user behind
/// the connected token, then walks the paginated climb-day feed and drills into
/// each day's logs to assemble the user's ticks. Query documents are kept local
/// and trimmed: the per-day drill-down deliberately omits competition-round
/// fields, whose inclusion makes TopLogger return HTTP 400 on populated days.
/// </summary>
public sealed class TopLoggerApiClient : ITopLoggerApiClient
{
    private const int DefaultPageSize = 50;

    // Resolves the TopLogger user id behind the current access token.
    private const string UserMeQuery =
        "query userMe { userProfile: userMe { id } }";

    // Trimmed climb-day feed (comp-round fields kept out).
    private const string SessionsQuery =
        "query climbDaysStravaList($userId: ID!, $pagination: PaginationInputClimbDays) {\n" +
        "  climbDaysPaginated(userId: $userId, totalTickedMin: 1, pagination: $pagination, updateDayStatsIfOld: true) {\n" +
        "    pagination { total page perPage }\n" +
        "    data { id statsAtDate gym { id name nameSlug } }\n" +
        "  }\n" +
        "}";

    // Per-day drill-down with competition-round fields removed (that removal is
    // what fixes the HTTP 400 on populated days).
    private const string DayLogsQuery =
        "query climbLogsForDay($userId: ID!, $climbedAtDate: DateTime) {\n" +
        "  climbLogs(userId: $userId, climbedAtDate: $climbedAtDate) {\n" +
        "    data { id climbId gymId climbType climbedAtDate tryIndex tickIndex ticked tickType " +
        "points topped zones\n" +
        "      climb { id name grade climbType holdColor { id color colorSecondary } " +
        "wall { id nameLoc } gym { id name nameSlug } } }\n" +
        "  }\n" +
        "}";

    private readonly ITopLoggerGraphQlClient client;
    private readonly ILogger<TopLoggerApiClient> logger;

    public TopLoggerApiClient(ITopLoggerGraphQlClient client, ILogger<TopLoggerApiClient> logger)
    {
        this.client = client;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TopLoggerTick>> GetTicksAsync(
        Guid userId,
        DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        string tlUserId = await ResolveUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Fetching TopLogger ticks for user {UserId} (TopLogger id {TlUserId}).", userId, tlUserId);
        List<TopLoggerTick> result = [];

        int page = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (IReadOnlyList<(DateTimeOffset? Date, string DateKey)> days, long total) =
                await LoadClimbDaysPageAsync(userId, tlUserId, page, cancellationToken).ConfigureAwait(false);
            if (days.Count == 0)
            {
                break;
            }

            bool reachedCutoff = await AppendDayTicksAsync(userId, tlUserId, days, since, result, cancellationToken)
                .ConfigureAwait(false);
            if (reachedCutoff || ((long)page * DefaultPageSize) >= total)
            {
                break;
            }

            page++;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<TopLoggerSessionSummary?> GetLatestSessionAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        string tlUserId = await ResolveUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        object variables = new
        {
            userId = tlUserId,
            pagination = new
            {
                page = 1,
                perPage = 1,
                orderBy = new[] { new { key = "statsAtDate", order = "desc" } },
            },
        };

        GraphQlResponse<JsonElement> response = await client
            .SendAsync<JsonElement>(userId, "climbDaysStravaList", SessionsQuery, variables, cancellationToken)
            .ConfigureAwait(false);
        EnsureNoErrors(response, "climbDaysStravaList");

        if (response.Data.TryObj("climbDaysPaginated", out JsonElement feed))
        {
            foreach (JsonElement day in feed.EnumerateArrayOrEmpty("data"))
            {
                DateTimeOffset? date = day.GetDateTimeOffsetOrNull("statsAtDate");
                if (date is { } d)
                {
                    return new TopLoggerSessionSummary(d, BuildDateKey(day));
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TopLoggerTick>> GetSessionTicksAsync(
        Guid userId,
        string sessionDateKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionDateKey))
        {
            return Array.Empty<TopLoggerTick>();
        }

        string tlUserId = await ResolveUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return await LoadDayTicksAsync(userId, tlUserId, sessionDateKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AppendDayTicksAsync(
        Guid userId,
        string tlUserId,
        IReadOnlyList<(DateTimeOffset? Date, string DateKey)> days,
        DateTimeOffset? since,
        List<TopLoggerTick> result,
        CancellationToken cancellationToken)
    {
        foreach ((DateTimeOffset? date, string dateKey) in days)
        {
            // The feed is ordered newest-first, so once a whole day predates the
            // cutoff there is nothing older worth fetching.
            if (since is { } cutoff && date is { } d && d.UtcDateTime.Date < cutoff.UtcDateTime.Date)
            {
                return true;
            }

            if (string.IsNullOrEmpty(dateKey))
            {
                continue;
            }

            IReadOnlyList<TopLoggerTick> dayTicks =
                await LoadDayTicksAsync(userId, tlUserId, dateKey, cancellationToken).ConfigureAwait(false);
            foreach (TopLoggerTick tick in dayTicks)
            {
                // Compare at day granularity (mirroring the whole-day cutoff above): climbedAtDate is
                // day-anchored, so an instant-level "< since" would drop ticks logged later on the same
                // day as the last sync. Re-including an already-imported tick is harmless — the import
                // dedupes on the tick's external id.
                if (since is { } c && tick.LoggedAt is { } loggedAt
                    && loggedAt.UtcDateTime.Date < c.UtcDateTime.Date)
                {
                    continue;
                }

                result.Add(tick);
            }
        }

        return false;
    }

    private async Task<string> ResolveUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        GraphQlResponse<JsonElement> response = await client
            .SendAsync<JsonElement>(userId, "userMe", UserMeQuery, null, cancellationToken)
            .ConfigureAwait(false);

        if (response.Data.TryObj("userProfile", out JsonElement profile))
        {
            string? id = profile.GetStringOrNull("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        throw new TopLoggerAuthException(
            userId, "Could not resolve the TopLogger user id for the connected session.");
    }

    private async Task<(IReadOnlyList<(DateTimeOffset? Date, string DateKey)> Days, long Total)> LoadClimbDaysPageAsync(
        Guid userId,
        string tlUserId,
        int page,
        CancellationToken cancellationToken)
    {
        object variables = new
        {
            userId = tlUserId,
            pagination = new
            {
                page,
                perPage = DefaultPageSize,
                orderBy = new[] { new { key = "statsAtDate", order = "desc" } },
            },
        };

        GraphQlResponse<JsonElement> response = await client
            .SendAsync<JsonElement>(userId, "climbDaysStravaList", SessionsQuery, variables, cancellationToken)
            .ConfigureAwait(false);
        EnsureNoErrors(response, "climbDaysStravaList");

        List<(DateTimeOffset?, string)> days = [];
        long total = 0;
        if (response.Data.TryObj("climbDaysPaginated", out JsonElement feed))
        {
            foreach (JsonElement day in feed.EnumerateArrayOrEmpty("data"))
            {
                DateTimeOffset? date = day.GetDateTimeOffsetOrNull("statsAtDate");
                days.Add((date, BuildDateKey(day)));
            }

            if (feed.TryObj("pagination", out JsonElement pagination))
            {
                total = pagination.GetInt64OrNull("total") ?? days.Count;
            }
        }

        return (days, total);
    }

    private async Task<IReadOnlyList<TopLoggerTick>> LoadDayTicksAsync(
        Guid userId,
        string tlUserId,
        string climbedAtDate,
        CancellationToken cancellationToken)
    {
        object variables = new { userId = tlUserId, climbedAtDate };
        GraphQlResponse<JsonElement> response = await client
            .SendAsync<JsonElement>(userId, "climbLogsForDay", DayLogsQuery, variables, cancellationToken)
            .ConfigureAwait(false);

        // A per-day fetch that comes back as a GraphQL ERROR must fail the whole pull
        // rather than be treated as an empty day: silently skipping it truncates the
        // import (e.g. a lingering throttle or server error) yet reports success.
        // A successful-but-empty day (data present, no errors) is not an error and
        // legitimately yields zero ticks below.
        EnsureNoErrors(response, "climbLogsForDay");

        List<TopLoggerTick> ticks = [];
        if (response.Data.TryObj("climbLogs", out JsonElement climbLogs))
        {
            foreach (JsonElement log in climbLogs.EnumerateArrayOrEmpty("data"))
            {
                ticks.Add(TopLoggerTickMapping.MapTick(log));
            }
        }

        return ticks;
    }

    /// <summary>
    /// Throws when a GraphQL response carried errors, so a genuine failure surfaces
    /// out of the tick pull instead of being swallowed into a truncated success. A
    /// persistent throttle has already thrown <see cref="TopLoggerThrottledException"/>
    /// from the client; this covers any other error the API returns mid-pagination.
    /// </summary>
    private static void EnsureNoErrors(GraphQlResponse<JsonElement> response, string operationName)
    {
        if (!response.HasErrors || response.Errors is null)
        {
            return;
        }

        string detail = string.Join("; ", response.Errors.Select(e => e.Message));
        throw new InvalidOperationException(
            $"TopLogger operation '{operationName}' returned an error: {detail}");
    }

    private static string BuildDateKey(JsonElement day)
    {
        string? raw = day.GetRawTextOrNull("statsAtDate");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        int tIndex = raw.IndexOf('T', StringComparison.Ordinal);
        return tIndex > 0 ? raw[..tIndex] : raw;
    }
}
