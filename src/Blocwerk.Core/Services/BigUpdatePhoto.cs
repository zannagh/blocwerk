namespace Blocwerk.Core.Services;

/// <summary>
/// One photo of a big-wall update capture, placed on the sparse panel grid. Exactly one photo
/// must be the centre at (0,0); the rest are its orthogonal neighbours.
/// </summary>
/// <param name="Image">Encoded image bytes (JPEG/PNG).</param>
/// <param name="ContentType">The image MIME type, e.g. <c>image/jpeg</c>.</param>
/// <param name="Col">Grid column; 0 is the centre panel.</param>
/// <param name="Row">Grid row; 0 is the centre panel.</param>
public record BigUpdatePhoto(byte[] Image, string ContentType, int Col, int Row);
