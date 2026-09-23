using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>Outcome of a geometry upload: either the stored model, or the reasons it was refused.</summary>
public sealed record GeometryImportResult(WallGeometryModel? Model, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Model is not null && Errors.Count == 0;

    public static GeometryImportResult Fail(params string[] errors) => new(null, errors);

    public static GeometryImportResult Fail(IReadOnlyList<string> errors) => new(null, errors);

    public static GeometryImportResult Ok(WallGeometryModel model) => new(model, []);
}
