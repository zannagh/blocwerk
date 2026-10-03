namespace Blocwerk.Core.Capture;

/// <summary>
/// Resolves a solved camera's <c>image</c> name back to its capture photo. The geometry worker only ever sees
/// the photos as <see cref="CaptureComputeDocuments.PhotoName"/> (<c>p01</c>, <c>p02</c>…), so that is what its
/// cameras are called; the original file name (<c>IMG_2787.HEIC</c>) never reaches it. Base file names are
/// mapped too, for models solved from files named after the originals; the worker's name wins a clash.
/// </summary>
public static class CameraPhotoNames
{
    /// <summary>Camera name → stored path, case-insensitive.</summary>
    /// <param name="photos">The capture's photos: index, original file name (optional) and stored path.</param>
    /// <returns>The lookup.</returns>
    public static Dictionary<string, string> Map(IEnumerable<(int Index, string? OriginalFileName, string StoredPath)> photos)
    {
        var list = photos.OrderBy(p => p.Index).ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
        {
            map.TryAdd(CaptureComputeDocuments.PhotoName(p.Index), p.StoredPath);
        }

        foreach (var p in list.Where(p => !string.IsNullOrEmpty(p.OriginalFileName)))
        {
            map.TryAdd(Path.GetFileNameWithoutExtension(p.OriginalFileName!), p.StoredPath);
        }

        return map;
    }
}
