using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Fields every replayable offline action carries. <see cref="ClientRequestId"/> is generated
/// on the client at enqueue time and reused for every retry, which is what makes a replay
/// idempotent server-side.
/// </summary>
public abstract class OfflineActionRequest
{
    public Guid BoulderId { get; set; }

    public Guid? ClientRequestId { get; set; }

    /// <summary>
    /// The user who was signed in when the action was queued, stamped by the client at enqueue
    /// time. Replay resolves identity from the cookie, which on a shared device (a kiosk tablet,
    /// a shared laptop) can be a different person by the time the queue drains — see
    /// <see cref="OfflineActionOwnership"/>. Null on entries queued before stamping existed.
    /// </summary>
    public Guid? QueuedForUserId { get; set; }
}

public sealed class LogAttemptRequest : OfflineActionRequest
{
    /// <summary>
    /// Accepts the name ("Attempt"/"Send"/"Flash") as well as the numeric value. The DOM
    /// contract puts the name in <c>data-bw-type</c>, which is far easier to read in markup
    /// than a magic number, so string parsing is required rather than optional.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AttemptType>))]
    public AttemptType Type { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// The moment the user actually tapped, captured on the client at enqueue time. A queued
    /// attempt can replay long after the tap; anchoring the stored timestamp and the server-side
    /// debounce window on this real time (rather than replay time) is what keeps a batch of
    /// genuinely distinct offline attempts from collapsing into one. Omitted means "now".
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }
}

public sealed class SetRatingRequest : OfflineActionRequest
{
    public int Stars { get; set; }
}

public sealed class SetFavoriteRequest : OfflineActionRequest
{
    public bool Favorite { get; set; }
}

public sealed class AddCommentRequest : OfflineActionRequest
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Uniform envelope every offline endpoint returns, so the client queue can branch on
/// <see cref="Applied"/> without knowing the action type.
/// </summary>
/// <param name="Applied">True when the action is now reflected in server state, whether it
/// was applied by this call or by an earlier replay of the same client request id.</param>
/// <param name="ClientRequestId">Echo of the id the client sent, so the queue can match the
/// response to the queued entry even if responses arrive out of order.</param>
public record OfflineActionResponse(bool Applied, Guid? ClientRequestId, object? Result = null);

/// <summary>
/// Error envelope. <paramref name="Permanent"/> tells the client queue whether retrying could
/// ever succeed; when true the entry must be dropped and surfaced to the user.
/// </summary>
public record OfflineActionError(string Message, bool Permanent);
