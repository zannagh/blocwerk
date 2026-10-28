// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client;

/// <summary>
/// The offline client's schema: what a client build understands of the server's documents, feed and endpoints (plan
/// 2026-10-22 §3 "Deploy", PR 3.14). <c>/alive</c> sends <see cref="Minimum"/> as <c>clientSchemaMin</c>, and the shell
/// carries <see cref="Current"/> in its <c>bw-client-schema</c> meta. A client below the minimum asks the worker for the
/// deploy's version and reloads into it once (client-handback.js).
/// </summary>
public static class ClientSchema
{
    /// <summary>
    /// The schema this build speaks. Raise it, together with <see cref="Minimum"/>, when a document, feed event or
    /// endpoint the client reads changes in a way an older client would misread.
    /// </summary>
    public const int Current = 1;

    /// <summary>
    /// The oldest client schema this server still serves correctly. Never above <see cref="Current"/>; raising it makes
    /// every older client on a device update and reload once.
    /// </summary>
    public const int Minimum = 1;
}
