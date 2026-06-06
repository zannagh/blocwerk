using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class WallMember
{
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public WallRole Role { get; set; } = WallRole.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}
