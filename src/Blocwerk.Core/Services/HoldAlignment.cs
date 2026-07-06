using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Applies a normalized (0-1) homography to a hold's geometry. Shared by the
/// wall auto-align flow and the in-editor preview so both stay consistent.
/// </summary>
public static class HoldAlignment
{
    public static void Apply(Hold hold, Homography h)
    {
        var (nx, ny) = h.Project(hold.X, hold.Y);

        // Local scale from projecting a small step along each axis.
        var step = Math.Max(hold.Radius, 0.01);
        var (sxx, sxy) = h.Project(hold.X + step, hold.Y);
        var (syx, syy) = h.Project(hold.X, hold.Y + step);
        var scaleX = Math.Sqrt(((sxx - nx) * (sxx - nx)) + ((sxy - ny) * (sxy - ny))) / step;
        var scaleY = Math.Sqrt(((syx - nx) * (syx - nx)) + ((syy - ny) * (syy - ny))) / step;
        var scale = (scaleX + scaleY) / 2.0;

        if (hold.ShapePoints is { Count: > 0 })
        {
            foreach (var sp in hold.ShapePoints)
            {
                var (px, py) = h.Project(hold.X + sp.Dx, hold.Y + sp.Dy);
                sp.Dx = px - nx;
                sp.Dy = py - ny;
            }
        }

        hold.X = Math.Clamp(nx, 0, 1);
        hold.Y = Math.Clamp(ny, 0, 1);
        hold.Radius = Math.Clamp(hold.Radius * scale, 0.003, 0.2);
    }
}
