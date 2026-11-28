// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>The consent form of <c>/device-link</c>: the app's request, carried through, and the button pressed.</summary>
public sealed class DeviceLinkDecision
{
    [FromForm(Name = "state")]
    public string? State { get; set; }

    [FromForm(Name = "code_challenge")]
    public string? CodeChallenge { get; set; }

    [FromForm(Name = "code_challenge_method")]
    public string? CodeChallengeMethod { get; set; }

    [FromForm(Name = "device")]
    public string? Device { get; set; }

    [FromForm(Name = "redirect_uri")]
    public string? RedirectUri { get; set; }

    /// <summary>"link" or "cancel"; anything else is a cancel.</summary>
    [FromForm(Name = "decision")]
    public string? Decision { get; set; }
}
