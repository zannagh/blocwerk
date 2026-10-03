using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="CameraPhotoNames"/>: the model's cameras are named by <see cref="CaptureComputeDocuments.PhotoName"/>
/// (the only name the geometry worker sees), so the footprint refinement must find the capture photos by it.
/// </summary>
public class CameraPhotoNamesTests
{
    [Fact]
    public void CamerasNamedByTheWorkerName_ResolveToTheirCapturePhotos()
    {
        var map = CameraPhotoNames.Map(
        [
            (1, "IMG_2787.HEIC", "a.heic"),
            (2, "IMG_2788.jpg", "b.jpg"),
            (10, null, "c.jpg"),
        ]);

        Assert.Equal("a.heic", map[CaptureComputeDocuments.PhotoName(1)]);
        Assert.Equal("b.jpg", map["p02"]);
        Assert.Equal("c.jpg", map["p10"]);

        // models solved from original file names still resolve
        Assert.Equal("a.heic", map["IMG_2787"]);
    }

    [Fact]
    public void TheWorkerName_WinsAClashWithAnOriginalFileName()
    {
        var map = CameraPhotoNames.Map([(1, "p02.jpg", "first.jpg"), (2, "IMG_1.jpg", "second.jpg")]);

        Assert.Equal("second.jpg", map["p02"]);
        Assert.Equal("first.jpg", map["p01"]);
    }
}
