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

        Assert.Equal(
            $"/media/walls/{wall}/gallery/resetphoto/{id}?w=640",
            ResponsiveImage.Gallery(wall, "ResetPhoto", id, null, thumbnail: true));
        Assert.Equal(
            $"/media/walls/{wall}/gallery/wallphoto/{id}?token=a%2Fb&w=640",
            ResponsiveImage.Gallery(wall, "WallPhoto", id, "a/b", thumbnail: true));
        Assert.Equal(
            $"/media/walls/{wall}/gallery/uploaded/{id}",
            ResponsiveImage.Gallery(wall, "Uploaded", id, null, thumbnail: false));
    }
}
