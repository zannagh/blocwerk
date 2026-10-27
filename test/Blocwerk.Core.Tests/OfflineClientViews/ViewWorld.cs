// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Tests.OfflineClientStore;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// A cached wall for the views' tests, as the server writes its documents: two live panels (the right one a generation
/// behind), three holds, a published boulder with two comments, an archived one and a draft, and the viewer's marks.
/// </summary>
internal static class ViewWorld
{
    public static readonly Guid PanelLeft = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid PanelRight = Guid.Parse("a0000000-0000-0000-0000-00000000000b");
    public static readonly Guid HoldStart = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid HoldTop = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    public static readonly Guid HoldRight = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    public static readonly Guid HoldVirtual = Guid.Parse("d0000000-0000-0000-0000-000000000004");
    public static readonly Guid HoldVirtual2 = Guid.Parse("d0000000-0000-0000-0000-000000000005");
    public static readonly Guid VirtualTop = Guid.Parse("b4444444-4444-4444-4444-444444444444");
    public static readonly Guid AllVirtual = Guid.Parse("b5555555-5555-5555-5555-555555555555");
    public static readonly Guid Crimp = Boulder1;
    public static readonly Guid Slab = Boulder2;
    public static readonly Guid Draft = Guid.Parse("b3333333-3333-3333-3333-333333333333");
    public static readonly FeedActor Bob = new(Guid.Parse("a2222222-2222-2222-2222-222222222222"), "Bob", true, null);

    public static string PanelSha(char c) => new(c, 64);

    public static WallDocument WallDoc() => new(1, Wall, "The Attic", "Steep and short", 30, 2, null, null, [],
    [
        new WallDocumentPanel(PanelLeft, 0, 0, 2, PanelSha('1'), "image/jpeg"),
        new WallDocumentPanel(PanelRight, 1, 0, 1, PanelSha('2'), "image/jpeg"),
    ]);

    public static HoldsDocument HoldsDoc() => new(1, Wall, 2,
    [
        Hold(HoldStart, PanelLeft, 0.2, 0.8),
        Hold(HoldTop, PanelLeft, 0.5, 0.1),
        Hold(HoldRight, PanelRight, 0.5, 0.5),
    ]);

    public static BouldersDocumentBoulder CrimpLine() => new(
        Crimp, "Crimp Line", "6A", 2, null, false, false, At, At, "Alice", true, false, false, "red",
        new BouldersDocumentStats(7, 2, 4.5),
        [new BouldersDocumentHold(HoldStart, "Start", "HandAndFoot"), new BouldersDocumentHold(HoldTop, "Top", "HandAndFoot"), new BouldersDocumentHold(HoldRight, "Normal", "FootOnly")]);

    public static BouldersDocumentBoulder SlabArchived() => new(
        Slab, "Old Slab", "7A", 1, null, true, true, At.AddDays(-30), At.AddDays(-30), "Bob", false, false, false, null,
        new BouldersDocumentStats(1, 0, null), [new BouldersDocumentHold(HoldRight, "Start", "HandAndFoot")]);

    public static BouldersDocumentBoulder DraftBoulder() => new(
        Draft, "Work in Progress", null, 2, true, false, false, At.AddDays(1), null, "Alice", false, false, false, null,
        new BouldersDocumentStats(0, 0, null), [new BouldersDocumentHold(HoldTop, "Normal", "HandAndFoot")]);

    public static CommentsDocumentComment CommentBy(Guid id, FeedActor author, string text, int minutes) =>
        new(id, Crimp, author, text, At.AddMinutes(minutes));

    public static IReadOnlyList<CommentsDocumentComment> Comments() =>
    [
        CommentBy(CommentA, Alice, "Lovely crimps", 0),
        CommentBy(CommentB, Bob, "The top is spicy", 5),
    ];

    public static StoreMineRow Marks() => new(Wall, "1.2.3",
        [new MineDocumentTick(Crimp, 3, true, false, At), new MineDocumentTick(Slab, 1, true, true, At)],
        [Crimp]);

    /// <summary>A ready device under <paramref name="partition"/> holding the wall (marks only for a user).</summary>
    public static FakeStoreSource CachedDevice(string partition, IReadOnlyList<CommentsDocumentComment>? comments = null, bool withDraft = true)
    {
        var source = new FakeStoreSource();
        BouldersDocumentBoulder[] boulders = withDraft ? [CrimpLine(), SlabArchived(), DraftBoulder()] : [CrimpLine(), SlabArchived()];
        Put(source, partition, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Json(WallDoc())),
            [DataDocumentKinds.Holds] = new(Sha('h'), Json(HoldsDoc())),
            [DataDocumentKinds.Boulders] = new(Sha('b'), Json(new BouldersDocument(1, Wall, boulders))),
            [DataDocumentKinds.Comments] = new(Sha('c'), Json(new CommentsDocument(1, Wall, comments ?? Comments()))),
        },
        shareWall: partition == Anon ? Wall : null,
        mine: partition.StartsWith("u-", StringComparison.Ordinal) ? [Marks()] : null);
        return source;
    }

    /// <summary>
    /// The wall with two never-stamped virtual holds (no panel, as the holds document keeps them): a boulder whose top
    /// is virtual (its start on the right panel), and one made of virtual holds only.
    /// </summary>
    public static FakeStoreSource VirtualDevice(string partition)
    {
        var source = new FakeStoreSource();
        var holds = HoldsDoc() with { Holds = [.. HoldsDoc().Holds, Virtual(HoldVirtual, 0.7, 0.3), Virtual(HoldVirtual2, 0.6, 0.6)] };
        BouldersDocumentBoulder[] boulders =
        [
            CrimpLine() with { Id = VirtualTop, Name = "Virtual Top", Holds = [new(HoldRight, "Start", "HandAndFoot"), new(HoldVirtual, "Top", "HandAndFoot")] },
            CrimpLine() with { Id = AllVirtual, Name = "All Virtual", Holds = [new(HoldVirtual, "Start", "HandAndFoot"), new(HoldVirtual2, "Top", "HandAndFoot")] },
        ];
        Put(source, partition, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Json(WallDoc())),
            [DataDocumentKinds.Holds] = new(Sha('h'), Json(holds)),
            [DataDocumentKinds.Boulders] = new(Sha('b'), Json(new BouldersDocument(1, Wall, boulders))),
            [DataDocumentKinds.Comments] = new(Sha('c'), Json(new CommentsDocument(1, Wall, []))),
        });
        return source;
    }

    /// <summary>A ready device holding only the given kinds of the wall's documents (the others never cached).</summary>
    public static FakeStoreSource PartialDevice(string partition, params string[] kinds)
    {
        var full = CachedDevice(partition).Docs[(partition, Wall)].Docs;
        var source = new FakeStoreSource();
        Put(source, partition, kinds.ToDictionary(k => k, k => full[k]), mine: partition.StartsWith("u-", StringComparison.Ordinal) ? [Marks()] : null);
        return source;
    }

    public static string CommentsJson(IReadOnlyList<CommentsDocumentComment> comments) => Json(new CommentsDocument(1, Wall, comments));

    private static HoldsDocumentHold Virtual(Guid id, double x, double y) =>
        new(id, null, 2, x, y, 0.03, null, "Hand", null, false, true, false, null, null, null, null);

    private static HoldsDocumentHold Hold(Guid id, Guid panel, double x, double y) =>
        new(id, panel, 2, x, y, 0.03, "red", "Hand", null, false, false, false, null, null, null, null);
}
