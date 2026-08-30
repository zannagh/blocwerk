using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>Uniform error body of the machine API, so a device only has to parse one shape.</summary>
public record ApiErrorResponse(string Message);

/// <summary>One temperature sample as the API exposes it.</summary>
public record TemperatureReadingResponse(DateTimeOffset RecordedAt, double TemperatureCelsius);

/// <summary>Toggles a wall's "update mode" (maintenance) over the API.</summary>
public record WallMaintenanceRequest(bool Enabled);

/// <summary>
/// One gallery entry. <paramref name="Source"/> is the discriminator the caller has to put back
/// into the content route, because the three sources live in three different stores.
/// </summary>
public record WallGalleryItemResponse(
    Guid Id,
    string Source,
    string ContentType,
    long SizeBytes,
    string? Caption,
    DateTimeOffset CapturedAt);

/// <summary>Answer of a successful image upload.</summary>
public record WallImageCreatedResponse(Guid Id, DateTimeOffset CapturedAt);

/// <summary>Maps the Core projections onto the wire contracts above.</summary>
internal static class WallApiMappings
{
    public static WallGalleryItemResponse ToResponse(this WallGalleryItem item)
    {
        return new WallGalleryItemResponse(
            item.Id,
            item.Source.ToString(),
            item.ContentType,
            item.SizeBytes,
            item.Caption,
            item.CapturedAt);
    }
}
