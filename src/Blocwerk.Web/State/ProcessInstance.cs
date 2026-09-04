namespace Blocwerk.Web.State;

/// <summary>
/// Identifies THIS server process. Generated once, in a static initializer, and stable for as long
/// as the process lives.
/// </summary>
/// <remarks>
/// The whole point of the "server is updating" flow. A browser cannot tell "the socket dropped" from
/// "the container was replaced" — both look like a failed reconnect — and neither a version number
/// nor a build stamp helps, because a redeploy of the same image would keep them identical. A value
/// minted at startup changes on every recreate and on nothing else, so a client that captured it
/// once and later reads a DIFFERENT one has PROOF that a new process is serving and it is safe to
/// reload. Same id, however long the gap: the old container is still there, so keep waiting.
/// </remarks>
public static class ProcessInstance
{
    /// <summary>The id of the currently running process, as a lowercase 32-char hex string.</summary>
    public static readonly string Id = Guid.NewGuid().ToString("N");

    /// <summary>When this process started, for a human reading the endpoint by hand.</summary>
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
}
