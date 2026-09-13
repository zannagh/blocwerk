using System.Collections.Concurrent;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Development-only in-memory store keeping the last big-update <see cref="BigUpdateSession"/> per
/// wall. The staged HOLDS live in the DB (at the staged generation), so metrics and overlay reads
/// go straight to Postgres; only the matcher's PROPOSAL PAIRS are transient (they exist solely in
/// the returned session), so the promote and side-by-side endpoints recover them from here.
/// A static dictionary is deliberate: the dev harness is single-process and single-wall at a time.
/// </summary>
internal static class DevBigUpdateStore
{
    private static readonly ConcurrentDictionary<Guid, BigUpdateSession> Sessions = new();

    public static void Set(Guid wallId, BigUpdateSession session) => Sessions[wallId] = session;

    public static BigUpdateSession? Get(Guid wallId) =>
        Sessions.TryGetValue(wallId, out var session) ? session : null;

    public static void Clear(Guid wallId) => Sessions.TryRemove(wallId, out _);
}
