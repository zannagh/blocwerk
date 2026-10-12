// <copyright file="ApiDocsData.HoldColors.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for "Suggest colours for holds without one" over the API (WallHoldColorsApiController).</summary>
internal static partial class ApiDocsData
{
    private const string HoldColorsIntro =
        "Proposes a colour for every live hold of the wall that has none, from the panel photo it sits on. A panel update does "
        + "this for its new holds when it is applied; this route does it for holds that are already live. A hold that has a colour is "
        + "never touched, and a hold the estimate is not sure about stays without one, so it is safe to repeat. A personal key with "
        + "write access whose owner is an admin of the wall; wall, kiosk and installation keys get 403.";

    private const string HoldColorsJson =
        "{\n  \"considered\": 124,\n  \"proposed\": 71,\n  \"fromLinked\": 3,\n  \"leftUnset\": 50,\n  "
        + "\"proposedByColor\": { \"blue\": 14, \"yellow\": 19 },\n  \"leftUnsetByReason\": { \"BetweenColors\": 12, \"MostlyChalk\": 9 }\n}";

    private const string HoldColorsNote =
        "fromLinked are holds that took the colour of the same hold on another panel. Proposed colours say \"Suggested\" in the "
        + "hold editor until somebody keeps or changes them.";

    private static ApiSurfaceDoc WallHoldColorsSurface => new(
        "Hold colours",
        HoldColorsIntro,
        "Personal key with write access (admin owner)",
        [
            new("POST", "/api/walls/{wallId}/holds/suggest-colors", "Suggests colours for the live holds without one.",
                [new("wallId", "path", "The wall.")], null, HoldColorsJson, HoldColorsNote),
        ]);
}
