using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class User : IEquatable<User>
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(512)]
    public required string Identifier { get; set; }

    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    public IdentityRole Role { get; set; } = IdentityRole.User;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int ProgressionWindowDays { get; set; } = 60;

    public ICollection<WallMember> WallMemberships { get; set; } = [];

    public ICollection<Attempt> Attempts { get; set; } = [];

    public ICollection<HangboardSession> HangboardSessions { get; set; } = [];

    public ICollection<PullupSession> PullupSessions { get; set; } = [];

    public string UserName => Identifier.Split("__").FirstOrDefault() ?? Identifier;

    public string UserAuthId => Identifier.Split("__").LastOrDefault() ?? Identifier;

    public bool Equals(User? other) => other is not null && Id == other.Id;

    public override bool Equals(object? obj) => Equals(obj as User);

    public override int GetHashCode() => Id.GetHashCode();
}
