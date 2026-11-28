// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's revise body (<c>PUT /api/v1/walls/{w}/boulders/{b}</c>). Unlike the offline queue's full form
/// snapshot (<see cref="ReviseBoulderRequest"/>), every field is optional and a field left out (or null) keeps the
/// boulder's current value, so a partial save never erases setters, rule flags, the foot colour or a draft's holds.
/// Clearing is explicit: <c>""</c> clears the grade, foot colour or custom rule, <c>[]</c> clears the setters or hold types.
/// </summary>
public sealed class DeviceReviseBoulderRequest
{
    /// <summary>The replay key; required on the companion app's setter routes.</summary>
    public Guid? ClientRequestId { get; set; }

    public string? Name { get; set; }

    /// <summary>A Font or V grade; "" clears it.</summary>
    public string? Grade { get; set; }

    public bool? KickboardFootholdsOn { get; set; }

    public bool? HandsFollowFeet { get; set; }

    /// <summary>The only colour feet may use; "" clears it.</summary>
    public string? FootColorOnly { get; set; }

    public bool? NoMatch { get; set; }

    /// <summary>The custom rule; "" clears it.</summary>
    public string? CustomRule { get; set; }

    /// <summary>The style tags as <c>BoulderStyle</c> flags; 0 clears them.</summary>
    public int? Styles { get; set; }

    /// <summary>The hold types by name; an empty list clears them.</summary>
    public List<string?>? HoldTypes { get; set; }

    /// <summary>The boulder's holds, replacing the stored ones; never empty.</summary>
    public List<BoulderHoldDto>? Holds { get; set; }

    /// <summary>The setters as wall-member user ids, replacing the stored ones; an empty list clears them.</summary>
    public List<Guid>? Setters { get; set; }
}
