using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// One hold mark in an offline boulder snapshot. Enum members are accepted by name
/// ("Normal"/"Start"/"HandAndFoot"/...) as well as by numeric value, so the client can serialize
/// the same <see cref="BoulderHoldInput"/> shape it already binds in the form.
/// </summary>
public sealed class BoulderHoldDto
{
    public Guid HoldId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<HoldType>))]
    public HoldType Type { get; set; } = HoldType.Normal;

    [JsonConverter(typeof(JsonStringEnumConverter<HoldUsage>))]
    public HoldUsage Usage { get; set; } = HoldUsage.HandAndFoot;

    public BoulderHoldInput ToInput() => new(HoldId, Type, Usage);
}

/// <summary>
/// Full form snapshot the client captures at boulder-create submit time. <see cref="Id"/> is
/// minted on the client so create is an idempotent upsert on it; <see cref="ClientRequestId"/>
/// is carried for parity with the other offline actions and to line the entry up with its queue
/// row. Replaying the same snapshot never creates a second boulder.
/// </summary>
public sealed class CreateBoulderRequest
{
    public Guid Id { get; set; }

    public Guid WallId { get; set; }

    public Guid? ClientRequestId { get; set; }

    /// <summary>
    /// The user who was signed in when the snapshot was queued. See <see cref="OfflineActionOwnership"/>:
    /// a create replayed under someone else would credit them as the boulder's setter.
    /// </summary>
    public Guid? QueuedForUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Grade { get; set; }

    public bool IsDraft { get; set; }

    public bool KickboardFootholdsOn { get; set; } = true;

    public bool HandsFollowFeet { get; set; } = true;

    public string? FootColorOnly { get; set; }

    public bool NoMatch { get; set; }

    public string? CustomRule { get; set; }

    /// <summary>The style tags as <c>BoulderStyle</c> flags; 0 or absent means none.</summary>
    public int Styles { get; set; }

    public List<BoulderHoldDto> Holds { get; set; } = [];

    /// <summary>
    /// The boulder's setter(s), as wall-member user ids. Non-members are skipped server-side. Empty
    /// means no explicit setter.
    /// </summary>
    public List<Guid> Setters { get; set; } = [];

    /// <summary>The boulder this one was created as a variant of, if any.</summary>
    public Guid? VariantOfBoulderId { get; set; }

    public List<BoulderHoldInput> ToInputs() => Holds.Select(h => h.ToInput()).ToList();
}

/// <summary>
/// Form snapshot the client captures at boulder-revise submit time. The boulder id travels in the
/// route; revise replaces the boulder's holds and fields, so re-applying the same snapshot is a
/// no-op (see <see cref="Blocwerk.Core.Services.BoulderService"/>).
/// </summary>
public sealed class ReviseBoulderRequest
{
    public Guid? ClientRequestId { get; set; }

    /// <summary>
    /// The user who was signed in when the snapshot was queued. See <see cref="OfflineActionOwnership"/>:
    /// revise is permission-checked against the caller, so replaying it under the wrong person is
    /// both a wrong-attribution and a wrong-permission decision.
    /// </summary>
    public Guid? QueuedForUserId { get; set; }

    public string? Name { get; set; }

    public string? Grade { get; set; }

    public bool KickboardFootholdsOn { get; set; } = true;

    public bool HandsFollowFeet { get; set; } = true;

    public string? FootColorOnly { get; set; }

    public bool NoMatch { get; set; }

    /// <summary>The custom rule; null (a snapshot queued before it existed) leaves it as stored, "" clears it.</summary>
    public string? CustomRule { get; set; }

    /// <summary>The style tags as <c>BoulderStyle</c> flags; absent leaves the boulder's tags as they are.</summary>
    public int? Styles { get; set; }

    /// <summary>
    /// The single hold type a 0.7.0 client sends, by name; read only when <see cref="HoldTypes"/> is absent. It speaks for the
    /// grips alone: a name makes it the only grip, "" clears the grips, the hooks stay (<c>BoulderHoldTypeChange.FromLegacy</c>).
    /// </summary>
    public string? HoldType { get; set; }

    /// <summary>The hold types by name; absent leaves them as stored, an empty list clears them (<c>BoulderHoldTypeChange.FromNames</c>).</summary>
    public List<string?>? HoldTypes { get; set; }

    /// <summary>The boulder's holds; null (a metadata-only save) leaves the stored holds and the review state alone.</summary>
    public List<BoulderHoldDto>? Holds { get; set; } = [];

    /// <summary>
    /// The boulder's setter(s), as wall-member user ids. On revise this REPLACES the stored set
    /// (empty clears it); non-members are skipped server-side.
    /// </summary>
    public List<Guid> Setters { get; set; } = [];

    public List<BoulderHoldInput>? ToInputs() => Holds?.Select(h => h.ToInput()).ToList();
}
