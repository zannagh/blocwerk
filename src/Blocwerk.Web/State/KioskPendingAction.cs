namespace Blocwerk.Web.State;

/// <summary>
/// An ascent that was tapped on a kiosk boulder page while nobody was picked, carried through the
/// sign-in so it can be logged once an identity exists.
/// </summary>
/// <remarks>
/// Every field is pure user input — it arrives from a link the ANONYMOUS tablet rendered — so
/// nothing here is authority for anything. The boulder is re-checked against the wall in the device
/// cookie, and <see cref="UserId"/> is only ever compared against the user who actually signed in.
/// </remarks>
/// <param name="BoulderId">The boulder the ascent was tapped on.</param>
/// <param name="UserId">The member the tapper picked, and the only one it may be logged for.</param>
/// <param name="Type">The raw attempt type text; parsed and length-capped, never trusted.</param>
public sealed record KioskPendingAction(Guid? BoulderId, Guid? UserId, string? Type)
{
    /// <summary>
    /// The longest attempt type text accepted. Every real member of the enum is far shorter; the
    /// cap exists because this value is logged and reflected into a redirect URL, and unbounded
    /// caller-supplied text has no business in either.
    /// </summary>
    public const int MaxTypeLength = 32;

    /// <summary>True when a boulder and a type are both present, whatever they turn out to be.</summary>
    public bool HasValue => BoulderId is not null && !string.IsNullOrWhiteSpace(Type);

    /// <summary>
    /// The type text trimmed and capped to <see cref="MaxTypeLength"/>, or null when there is none.
    /// </summary>
    public string? SafeType
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Type))
            {
                return null;
            }

            var trimmed = Type.Trim();
            return trimmed.Length <= MaxTypeLength ? trimmed : trimmed[..MaxTypeLength];
        }
    }

    /// <summary>
    /// The query string tail that re-attaches this pending action to a retry URL, or an empty
    /// string when there is nothing to carry. Always starts with <c>&amp;</c>.
    /// </summary>
    public string QueryTail()
    {
        if (BoulderId is not { } boulderId || SafeType is not { } type)
        {
            return string.Empty;
        }

        var tail = $"&pendingBoulderId={boulderId}&pendingType={Uri.EscapeDataString(type)}";
        if (UserId is { } userId)
        {
            tail += $"&pendingUserId={userId}";
        }

        return tail;
    }
}
