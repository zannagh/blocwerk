namespace Blocwerk.Core.Enums;

public enum WallRole
{
    Member = 0,
    Admin = 1,

    /// <summary>
    /// Can use the wall hold editor (single- and multi-image walls) but cannot change wall
    /// settings, photos, shape, API keys, maintenance, or membership. Sits between
    /// <see cref="Member"/> and <see cref="Admin"/> in capability: owner ⊇ admin ⊇ moderator ⊇ member.
    /// </summary>
    Moderator = 2,
}
