using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One temperature sample for a wall, typically posted by a sensor authenticating with a
/// wall-scoped API key. <see cref="RecordedAt"/> is stamped server-side in UTC so a sensor with
/// a wrong clock cannot skew the series.
/// </summary>
public class WallTemperatureReading
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public double TemperatureCelsius { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
