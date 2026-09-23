using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture being processed (a detached snapshot the stages keep in step with the row), the
/// admin it runs as, and the marker layout it runs with (its plan snapshot, else the legacy ids).
/// </summary>
internal sealed record CaptureRun(WallCapture Capture, User User, WallMarkerLayout Layout);
