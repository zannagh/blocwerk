// <copyright file="SafeInputFile.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// <see cref="InputFile"/> whose first-render JS init cannot take the circuit down. Use it instead of
/// <c>&lt;InputFile&gt;</c> everywhere.
/// </summary>
/// <remarks>
/// The framework's <c>OnAfterRenderAsync</c> hands the element to <c>InputFile.init</c>, and the browser
/// resolves an <see cref="ElementReference"/> with <c>document.querySelector</c>. An input that is no longer
/// in the document by then resolves to <c>null</c>, init throws "Cannot set properties of null (setting
/// '_blazorInputFileNextFileId')", and an unhandled after-render exception terminates the whole circuit.
/// That window is real: an enhanced navigation away from a page swaps its DOM out while the page is still
/// loading, and a section that finishes loading just then (WallMarkerSettings on the wall page, the avatar
/// card on the profile) renders its inputs into nodes that are already gone. Such an input can never be
/// used, so a failed init is dropped instead of escalated.
/// </remarks>
public sealed class SafeInputFile : InputFile
{
    [Inject]
    private ILogger<SafeInputFile> Logger { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await base.OnAfterRenderAsync(firstRender);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is going away; there is nothing left to initialise.
        }
        catch (JSException ex)
        {
            // Warning, not Debug: the expected case is a detached input, but a live one whose init failed
            // would silently stop picking files, and that has to be visible in the logs.
            Logger.LogWarning(ex, "File input init failed; expected when its element left the document before init ran");
        }
    }
}
