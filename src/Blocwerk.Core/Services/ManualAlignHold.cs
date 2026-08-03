using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

public record ManualAlignHold(
    Guid StagedHoldId,
    double X,
    double Y,
    double Radius,
    List<ShapePoint>? ShapePoints,
    string? Color,
    HoldMaterial? Material,
    HoldCategory Category,
    bool IsOnKickboard,
    bool DidChange,
    bool IsNew);
