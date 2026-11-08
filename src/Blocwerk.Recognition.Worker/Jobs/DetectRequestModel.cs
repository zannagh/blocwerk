// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>The model a <see cref="DetectRequest"/> names: display name and lower-case hex SHA-256.</summary>
public sealed record DetectRequestModel(string Name, string Sha256);
