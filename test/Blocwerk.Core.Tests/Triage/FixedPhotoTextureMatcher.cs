// <copyright file="FixedPhotoTextureMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>Opens every photo as the <see cref="FakePhotoTextureMatcher"/> photo <paramref name="photo"/> (real JPEGs start with 255, which it cannot decode).</summary>
internal sealed class FixedPhotoTextureMatcher(FakePhotoTextureMatcher inner, byte photo) : IPhotoTextureMatcher
{
    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto) => inner.OpenPhoto([photo]);
}
