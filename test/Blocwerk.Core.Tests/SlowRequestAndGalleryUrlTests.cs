// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

public class SlowRequestAndGalleryUrlTests
{
    [Fact]
    public void ALongPollEndpoint_IsNotMeasuredAsASlowRequest()
    {
        var plain = new DefaultHttpContext();
        plain.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "plain"));

        var longPoll = new DefaultHttpContext();
        longPoll.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new LongPollEndpoint()), "claim"));

        Assert.True(SlowRequestLogging.IsMeasurable(plain));
        Assert.True(SlowRequestLogging.IsMeasurable(new DefaultHttpContext()));
        Assert.False(SlowRequestLogging.IsMeasurable(longPoll));
    }

    [Fact]
    public void AGalleryThumbnail_AsksForTheSmallRendition_AndTheOpenedPhotoForTheStoredOne()
    {
        var wall = Guid.NewGuid();
        var id = Guid.NewGuid();

        var v = (string source) => ResponsiveImage.GalleryVersion(source, id, 1000);

        Assert.Equal(
            $"/media/walls/{wall}/gallery/resetphoto/{id}?v={v("resetphoto")}&w=640",
            ResponsiveImage.Gallery(wall, "ResetPhoto", id, 1000, null, thumbnail: true));
        Assert.Equal(
            $"/media/walls/{wall}/gallery/wallphoto/{id}?token=a%2Fb&v={v("wallphoto")}&w=640",
            ResponsiveImage.Gallery(wall, "WallPhoto", id, 1000, "a/b", thumbnail: true));
        Assert.Equal(
            $"/media/walls/{wall}/gallery/uploaded/{id}?v={v("uploaded")}",
            ResponsiveImage.Gallery(wall, "Uploaded", id, 1000, null, thumbnail: false));
    }
}
