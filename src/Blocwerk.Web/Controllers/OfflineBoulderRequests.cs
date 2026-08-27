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

    public string Name { get; set; } = string.Empty;

    public string? Grade { get; set; }

    public bool IsDraft { get; set; }

    public bool KickboardFootholdsOn { get; set; } = true;

    public bool HandsFollowFeet { get; set; } = true;

    public string? FootColorOnly { get; set; }

    public bool NoMatch { get; set; }

    public List<BoulderHoldDto> Holds { get; set; } = [];

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

    public string? Name { get; set; }

    public string? Grade { get; set; }

    public bool KickboardFootholdsOn { get; set; } = true;

    public bool HandsFollowFeet { get; set; } = true;

    public string? FootColorOnly { get; set; }

    public bool NoMatch { get; set; }

    public List<BoulderHoldDto> Holds { get; set; } = [];

    public List<BoulderHoldInput> ToInputs() => Holds.Select(h => h.ToInput()).ToList();
}
