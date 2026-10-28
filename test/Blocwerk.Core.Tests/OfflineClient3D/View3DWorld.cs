// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Client.ThreeD;
using Blocwerk.Client.Views;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Tests.OfflineClientStore;
using Blocwerk.Core.Tests.OfflineClientViews;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>The views' world with PR 3.13's slots mounted, a device's 3D status and, optionally, a cached gallery.</summary>
internal static class View3DWorld
{
    public static readonly string DocSha = new('d', 64);

    public static Client3DStatus Ready { get; } = new("ready", DocSha, true);

    public static GalleryDocumentImage Image(int i, string? caption = null) =>
        new(Guid.Parse($"e0000000-0000-0000-0000-{i:D12}"), new string((char)('a' + (i % 6)), 64), "image/jpeg", 1000, caption, At.AddMinutes(-i));

    /// <summary>A ready device holding the wall, with a gallery document of <paramref name="images"/> when given.</summary>
    public static FakeStoreSource Device3D(string partition, IReadOnlyList<GalleryDocumentImage>? images = null)
    {
        var source = ViewWorld.CachedDevice(partition);
        if (images is not null)
        {
            var docs = source.Docs[(partition, Wall)].Docs.ToDictionary(d => d.Key, d => d.Value);
            docs[DataDocumentKinds.Gallery] = new(Sha('g'), Json(new GalleryDocument(1, Wall, images)));
            Put(source, partition, docs, source.Snapshot.ShareWall, source.Snapshot.Mine);
        }

        return source;
    }

    /// <summary>A started harness with the slots Program.cs registers, the 3D status given, and the toggle.</summary>
    public static Task<ViewHarness> StartAsync(FakeStoreSource source, string path, FakeClient3D client, View3DToggle? toggle = null) =>
        ViewHarness.StartAsync(source, path, ClientSlots.Register(new ViewSlotRegistry()), client, toggle);
}
