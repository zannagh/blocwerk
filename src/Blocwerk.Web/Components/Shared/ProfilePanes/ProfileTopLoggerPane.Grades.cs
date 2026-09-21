// <copyright file="ProfileTopLoggerPane.Grades.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// Grade resolution and per-gym points calibration for the TopLogger import pane.
/// </summary>
public partial class ProfileTopLoggerPane
{
    // ---- TopLogger grade resolution ----
    private string? GetGradeSelection(string rawGradeKey) =>
        tlGradeSelections.TryGetValue(rawGradeKey, out var value) ? value : null;

    private void SetGradeSelection(string rawGradeKey, string? value)
    {
        tlGradeSelections[rawGradeKey] = value;
    }

    // Loads the unmapped buckets the first time the disclosure is opened (ontoggle fires on open+close;
    // the null guard makes this a one-shot load per fresh list).
    private async Task OnResolveGradesToggle()
    {
        if (tlUnmapped == null)
        {
            tlUnmapped = await TopLogger.GetUnmappedGradesAsync(Viewer.Id, CancellationToken.None);
            StateHasChanged();
        }
    }

    private async Task ResolveGradeAsync(TopLoggerUnmappedGrade item)
    {
        var grade = GetGradeSelection(item.RawGrade);
        if (string.IsNullOrEmpty(grade))
        {
            return;
        }

        tlResolveBusy = true;
        tlError = null;
        try
        {
            var updated = await TopLogger.ResolveGradeMappingAsync(Viewer.Id, item.RawGrade, grade, CancellationToken.None);
            var rawLabel = string.IsNullOrEmpty(item.RawGrade) ? "(no grade)" : item.RawGrade;

            await ReloadTopLoggerStatusAsync();
            tlUnmapped = await TopLogger.GetUnmappedGradesAsync(Viewer.Id, CancellationToken.None);
            tlGradeSelections.Remove(item.RawGrade);

            await OnToast.InvokeAsync($"Mapped {rawLabel} → {grade} on {updated} ascent(s).");
        }
        catch (Exception)
        {
            tlError = "Couldn't resolve the grade. Please try again.";
        }
        finally
        {
            tlResolveBusy = false;
        }
    }

    // ---- TopLogger gym points calibration ----
    private string GetCalibPoints(string grade) =>
        tlPointInputs.TryGetValue(grade, out var value) ? value : string.Empty;

    private void SetCalibPoints(string grade, string? value)
    {
        tlPointInputs[grade] = value ?? string.Empty;
    }

    // Loads the user's gyms the first time the disclosure opens (ontoggle fires on open+close; the null
    // guard makes it a one-shot load).
    private async Task OnCalibrationToggle()
    {
        if (tlGyms == null)
        {
            tlGyms = await TopLogger.GetCalibratableGymsAsync(Viewer.Id, CancellationToken.None);
            StateHasChanged();
        }
    }

    private async Task OnCalibrationGymChange(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out var gymId))
        {
            tlSelectedGymId = gymId;
            await LoadCalibrationFormAsync(gymId);
        }
        else
        {
            tlSelectedGymId = null;
            tlPointInputs.Clear();
            tlFlashBonusInput = string.Empty;
        }
    }

    // Loads a gym's saved calibration into the editable form state (blank inputs where unset).
    private async Task LoadCalibrationFormAsync(Guid gymId)
    {
        var calibration = await TopLogger.GetGymCalibrationAsync(gymId, CancellationToken.None);
        tlPointInputs.Clear();
        foreach (var p in calibration.Points)
        {
            tlPointInputs[p.Grade] = p.Points.ToString();
        }

        tlFlashBonusInput = calibration.FlashBonusPoints > 0 ? calibration.FlashBonusPoints.ToString() : string.Empty;
    }

    private async Task SaveCalibrationAsync()
    {
        if (tlSelectedGymId is not { } gymId)
        {
            return;
        }

        tlCalibBusy = true;
        tlError = null;
        try
        {
            var points = new List<GymGradePointDto>();
            foreach (var grade in GradeScale.FontGrades)
            {
                if (tlPointInputs.TryGetValue(grade, out var text)
                    && int.TryParse(text, out var value) && value > 0)
                {
                    points.Add(new GymGradePointDto(grade, value));
                }
            }

            var bonus = int.TryParse(tlFlashBonusInput, out var b) && b > 0 ? b : 0;

            var updated = await TopLogger.SaveGymCalibrationAsync(gymId, points, bonus, Viewer.Id, CancellationToken.None);
            var gymName = tlGyms?.FirstOrDefault(g => g.Id == gymId)?.Name ?? "gym";

            await ReloadTopLoggerStatusAsync();
            await LoadCalibrationFormAsync(gymId);

            await OnToast.InvokeAsync($"Saved {gymName}; recalculated {updated} ascent(s).");
        }
        catch (Exception)
        {
            tlError = "Couldn't save the calibration. Please try again.";
        }
        finally
        {
            tlCalibBusy = false;
        }
    }

    // Drop unsaved edits: reload the saved calibration for the selected gym back into the form.
    private async Task DiscardCalibrationAsync()
    {
        if (tlSelectedGymId is { } gymId)
        {
            await LoadCalibrationFormAsync(gymId);
        }
    }
}
