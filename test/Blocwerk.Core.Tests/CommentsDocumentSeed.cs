// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Comments over a seeded <see cref="DataDocumentFixture"/>: one on each boulder of the wall (published, draft, archived,
/// historic), by a plain member, the setter, an erased account (whose row still has a real-looking name), the Ghost user,
/// and the owner (who has an avatar); and one on a boulder of the other wall.
/// </summary>
internal sealed class CommentsDocumentSeed
{
    public const string ErasedName = "Erin Erased";

    public Guid ByMember { get; private init; }

    public Guid OnDraft { get; private init; }

    public Guid OnArchived { get; private init; }

    public Guid ByErased { get; private init; }

    public Guid ByGhost { get; private init; }

    public Guid ByOwner { get; private init; }

    public Guid OnOtherWall { get; private init; }

    public Guid ErasedId { get; private init; }

    /// <summary>Gets the comments every class reads: all of the wall's but the draft's.</summary>
    public IReadOnlyList<Guid> Everyones => [ByMember, OnArchived, ByErased, ByGhost, ByOwner];

    public static async Task<CommentsDocumentSeed> AddAsync(DataDocumentFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var erased = new User { Identifier = PlaceholderIdentity.DeletedIdentifier(Guid.NewGuid()), DisplayName = ErasedName, DeletedAt = DateTimeOffset.UtcNow };
        db.Users.Add(erased);
        var owner = await db.Users.FindAsync(f.Harness.Owner.Id);
        owner!.AvatarImage = [1, 2, 3];
        owner.AvatarContentType = "image/png";
        var other = new Boulder { WallId = f.OtherWallId, Name = "Elsewhere", CreatedByUserId = f.Harness.Owner.Id };
        db.Boulders.Add(other);
        var seed = new CommentsDocumentSeed
        {
            ByMember = Add(db, f.PublishedId, f.Member.Id, "member says the crimp is sharp"),
            OnDraft = Add(db, f.DraftId, f.Setter.Id, "draft secret: the start is wrong"),
            OnArchived = Add(db, f.ArchivedId, f.Member.Id, "archived but remembered"),
            ByErased = Add(db, f.HistoricId, erased.Id, "said before leaving"),
            ByGhost = Add(db, f.PublishedId, GhostUser.Id, "a system note"),
            ByOwner = Add(db, f.PublishedId, f.Harness.Owner.Id, "owner with an avatar"),
            OnOtherWall = Add(db, other.Id, f.Harness.Owner.Id, "another wall's comment"),
            ErasedId = erased.Id,
        };
        await db.SaveChangesAsync();
        return seed;
    }

    /// <summary>The caller's comments document, read through the service and parsed.</summary>
    public static Task<CommentsDocument> DocumentAsync(DataDocumentFixture f, string? token = null) =>
        f.DocumentAsync<CommentsDocument>(DataDocumentKinds.Comments, token);

    /// <summary>The caller's comments document as stored bytes, for scans.</summary>
    public static async Task<string> TextAsync(DataDocumentFixture f, string? token = null)
    {
        var index = await f.IndexAsync(token);
        var access = await f.AccessAsync(token);
        var sha = DataDocumentFixture.Sha(index, DataDocumentKinds.Comments);
        var stored = await f.Service().GetAsync(f.WallId, access.Audience!.Value, DataDocumentKinds.Comments, sha, default);
        return Encoding.UTF8.GetString(stored!.Json);
    }

    private static Guid Add(Blocwerk.Core.Data.BlocwerkDbContext db, Guid boulderId, Guid userId, string text)
    {
        var comment = new BoulderComment { BoulderId = boulderId, UserId = userId, Text = text };
        db.BoulderComments.Add(comment);
        return comment.Id;
    }
}
