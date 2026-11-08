// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary><c>texture-match</c>: <see cref="IPhotoTextureSession.Match"/> of part <c>photo</c> against <c>texture</c> (and its optional <c>mask</c>).</summary>
public sealed class TextureMatchHandler(IPhotoTextureMatcher matcher) : IRecognitionJobHandler
{
    public string Kind => "texture-match";

    public Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.Request<TextureMatchRequest>();
        var seed = request.CheckedSeed();
        var photo = envelope.Image("photo");
        var texture = envelope.Image("texture");
        var mask = envelope.Images("mask").Select(m => m.Bytes).SingleOrDefault();
        ct.ThrowIfCancellationRequested();
        try
        {
            using var session = matcher.OpenPhoto(photo);
            return Task.FromResult(ToResult(session.Match(texture, mask, request.TextureMmPerPx, seed, request.Attempt)));
        }
        catch (ArgumentException ex)
        {
            throw new RecognitionInputException($"The images cannot be matched: {ex.Message}");
        }
    }

    /// <summary>Contracts <c>jobs/recognition/texture-match-result</c>.</summary>
    public static object ToResult(PhotoTextureMatch match) => new
    {
        version = 1,
        pairs = match.Pairs,
        coarseInliers = match.CoarseInliers,
        failure = match.Failure,
    };
}
