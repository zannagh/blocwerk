// <copyright file="WallDetail.PanelCrop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Cropping the panel photo being edited (<see cref="Shared.PanelCropTool"/>): wall admins only, never with unsaved
/// hold edits pending (the crop re-maps the stored holds, so unsaved edits would land in the old frame). After a save
/// the panels are re-read, so the editor's photo URL carries the new revision and the holds come back re-mapped.
/// </summary>
public partial class WallDetail
{
    private bool croppingPanel;

    private bool CanCropPanel => _isAdmin && !_isAnonymous && _editingPanel is not null;

    private string EditingPanelPhotoUrl =>
        _editingPanel is null ? string.Empty : _editingPanel.WithPhotoRevision($"/api/walls/{WallId}/panels/{_editingPanel.Id}/photo");

    private async Task OpenCropTool()
    {
        if (_panelEditor?.HasUnsavedChanges == true)
        {
            await ShowToast("Save or discard your hold changes before cropping the photo");
            return;
        }

        croppingPanel = true;
    }

    private void CloseCropTool()
    {
        croppingPanel = false;
    }

    private async Task OnPanelCropSaved(int photoRevision)
    {
        // The revision arrives with the re-read panel below; the callback carries it for hosts without a panel list.
        _ = photoRevision;
        croppingPanel = false;
        var panelId = _editingPanel?.Id;
        await LoadPanels();
        _editingPanel = _livePanels.FirstOrDefault(p => p.Id == panelId) ?? _editingPanel;
        await LoadEditingPanelHolds();
        await ShowToast("Panel photo updated; holds stay where they are on the wall");
    }
}
