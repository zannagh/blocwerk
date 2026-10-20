// <copyright file="WallDetail.HoldTools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The editor's merge and split tools change holds on the server (one journal batch each); afterwards the wall and the
/// panel being edited are read again, exactly as after the join and make-actual tools.
/// </summary>
public partial class WallDetail
{
    private async Task OnHoldsRestructured()
    {
        await LoadWall();

        // On multi-image walls the editor works on the per-panel list, which LoadWall never touches.
        if (_editingPanel is not null)
        {
            await LoadEditingPanelHolds();
        }
    }
}
