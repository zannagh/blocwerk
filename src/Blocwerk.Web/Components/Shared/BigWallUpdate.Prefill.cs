using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Photos to pre-fill the upload step with (capture photos reused as panel photos).</summary>
public partial class BigWallUpdate
{
    /// <summary>Handed to the uploader; the rest of the flow is exactly the normal wall update.</summary>
    [Parameter]
    public IReadOnlyList<CaptureUpdatePhoto>? Prefill { get; set; }
}
