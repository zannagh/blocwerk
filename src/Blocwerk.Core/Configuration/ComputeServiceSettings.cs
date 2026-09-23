using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// One external compute worker speaking the Blocwerk compute job protocol v1
/// (<c>docker/compute-jobs-protocol.md</c>): a base URL plus an optional API key. Every worker —
/// the wall-geometry solver next to the app, a GPU splat worker elsewhere — is just another instance
/// of this, bound from its own env prefix (<c>GEOMETRYSERVICE__URL</c>, <c>SPLATSERVICE__URL</c>, …).
/// An empty URL means the feature is off.
/// </summary>
public class ComputeServiceSettings
{
    /// <summary>Base URL of the worker, e.g. <c>http://wall-geometry:8000</c>. Empty = not configured.</summary>
    public string? Url { get; set; }

    /// <summary>Sent as <c>Authorization: Bearer</c> when set. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Per-HTTP-request timeout (uploading many full-size photos takes a while).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one job may run before the app gives up on it.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A usable URL: <c>https://</c>, or plain <c>http://</c> only to a host that cannot be on the
    /// internet — loopback, or a single-label name such as the compose service <c>wall-geometry</c>.
    /// The API key and every wall photo travel over this connection.
    /// </summary>
    public bool IsConfigured => ConfigurationError is null && !string.IsNullOrWhiteSpace(Url);

    /// <summary>Why a non-empty <see cref="Url"/> is refused, or null when it is usable (or empty).</summary>
    public string? ConfigurationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                return null;
            }

            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "The compute service URL must be an absolute http(s) URL.";
            }

            return uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && uri.Host.Contains('.')
                ? $"The compute service URL must use https:// for a non-local host ({uri.Host}); plain http:// is "
                  + "only accepted for localhost or an internal single-label host name such as wall-geometry."
                : null;
        }
    }

    /// <summary>
    /// Binds <c>Blocwerk:{name}:*</c> or the <c>{ENVPREFIX}__URL / __APIKEY / __REQUESTTIMEOUTSECONDS /
    /// __JOBTIMEOUTMINUTES</c> environment variables.
    /// </summary>
    public static ComputeServiceSettings Bind(
        IConfigurationSection section, string name, string envPrefix, TimeSpan? defaultJobTimeout = null)
    {
        string? Read(string key, string env) =>
            section[$"{name}:{key}"] ?? Environment.GetEnvironmentVariable($"{envPrefix}__{env}");

        var settings = new ComputeServiceSettings
        {
            Url = Read("Url", "URL")?.Trim(),
            ApiKey = Read("ApiKey", "APIKEY")?.Trim(),
        };
        if (defaultJobTimeout is { } jobTimeout)
        {
            settings.JobTimeout = jobTimeout;
        }

        if (int.TryParse(Read("RequestTimeoutSeconds", "REQUESTTIMEOUTSECONDS"), out var seconds) && seconds > 0)
        {
            settings.RequestTimeout = TimeSpan.FromSeconds(seconds);
        }

        if (int.TryParse(Read("JobTimeoutMinutes", "JOBTIMEOUTMINUTES"), out var minutes) && minutes > 0)
        {
            settings.JobTimeout = TimeSpan.FromMinutes(minutes);
        }

        return settings;
    }
}
