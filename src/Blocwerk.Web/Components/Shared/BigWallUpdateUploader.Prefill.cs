using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Photos handed in from elsewhere (a capture's photos, picked as panel photos) fill their slots as if
/// the admin had picked them here. Nothing else changes: the admin still reviews the slots, can swap or
/// remove any of them, and Start hands the set to the same <c>OnStart</c> as ever.
/// </summary>
public partial class BigWallUpdateUploader
{
    private IReadOnlyList<CaptureUpdatePhoto>? appliedPrefill;

    /// <summary>Photos to pre-fill, each on its grid cell; a cell with no slot here is ignored.</summary>
    [Parameter]
    public IReadOnlyList<CaptureUpdatePhoto>? Prefill { get; set; }

    /// <summary>Fills each matching slot once per handed-in list, so a re-render never undoes the admin's edits.</summary>
    private void ApplyPrefill()
    {
        if (Prefill is null || ReferenceEquals(Prefill, appliedPrefill))
        {
            return;
        }

        appliedPrefill = Prefill;
        foreach (var photo in Prefill)
        {
            var slot = _slots.FirstOrDefault(s => s.Col == photo.Photo.Col && s.Row == photo.Photo.Row);
            if (slot is null)
            {
                continue;
            }

            slot.Bytes = photo.Photo.Image;
            slot.ContentType = photo.Photo.ContentType;
            slot.FileName = photo.FileName ?? "Capture photo";
            slot.Invalid = false;
            slot.Error = null;
        }
    }
}
