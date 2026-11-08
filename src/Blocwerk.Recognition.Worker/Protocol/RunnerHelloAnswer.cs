// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>The hello answer (contracts <c>runner/hello-response</c>); <c>Capabilities</c> is null before protocol 1.2.</summary>
public sealed record RunnerHelloAnswer(Guid RunnerId, string? Name, int PollSeconds, IReadOnlyList<string>? Capabilities);
