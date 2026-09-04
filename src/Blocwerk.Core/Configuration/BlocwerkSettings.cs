using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Blocwerk.Core.Configuration;

public class BlocwerkSettings
{
    public string JwtKey { get; private set; } = string.Empty;

    public TimeSpan JwtTokenLifetime { get; private set; } = TimeSpan.FromHours(1);

    public OAuthProviderSettings GitHubOAuth { get; private set; } = new();

    public OAuthProviderSettings GoogleOAuth { get; private set; } = new();

    public OAuthProviderSettings MicrosoftOAuth { get; private set; } = new();

    public ServerSettings Server { get; private set; } = new();

    public PostgresSettings Postgres { get; private set; } = new();

    /// <summary>
    /// Optional read-only source Postgres (e.g. production) used ONCE in Development by
    /// <c>DevDataImporter</c> to clone data into the isolated dev database. Null when unset.
    /// </summary>
    public PostgresSettings? DevImport { get; private set; }

    public HoldDetectionSettings HoldDetection { get; private set; } = new();

    public BetaVideoSettings BetaVideo { get; private set; } = new();

    public WallImageSettings WallImage { get; private set; } = new();

    /// <summary>
    /// Outgoing SMTP mail settings. Vaultwarden-style: everything is env-configurable
    /// (<c>SMTP__HOST</c>, <c>SMTP__PORT</c>, ...). Empty by default; features must gate on
    /// <see cref="SmtpSettings.IsConfigured"/> before attempting to send.
    /// </summary>
    public SmtpSettings Smtp { get; private set; } = new();

    public List<string> AdminIdentifiers { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="BlocwerkSettings"/> class.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    [JsonConstructor]
    public BlocwerkSettings()
    {
    }

    public BlocwerkSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection("Blocwerk");

        var jwtKey = section["JwtKey"];
        if (string.IsNullOrEmpty(jwtKey))
        {
            jwtKey = Environment.GetEnvironmentVariable("JWT__KEY");
        }

        JwtKey = string.IsNullOrEmpty(jwtKey) ? GenerateRandomKey() : jwtKey;

        if (TimeSpan.TryParse(
                section["JwtTokenLifetime"] ?? Environment.GetEnvironmentVariable("SECURITY__TOKENLIFETIME"),
                out var lifetime))
        {
            JwtTokenLifetime = lifetime;
        }

        Server = new ServerSettings
        {
            Url = section["Server:Url"]
                  ?? Environment.GetEnvironmentVariable("SERVER__URL")
                  ?? "https://localhost:5001",
            Port = int.TryParse(
                section["Server:Port"] ?? Environment.GetEnvironmentVariable("SERVER__PORT"),
                out var port)
                ? port
                : 5001,
        };

        Postgres = new PostgresSettings
        {
            Host = section["Postgres:Host"] ?? Environment.GetEnvironmentVariable("POSTGRES__HOST") ?? "localhost",
            Port = int.TryParse(
                section["Postgres:Port"] ?? Environment.GetEnvironmentVariable("POSTGRES__PORT"),
                out var pgPort)
                ? pgPort
                : 5051,
            Database = section["Postgres:Database"] ?? Environment.GetEnvironmentVariable("POSTGRES__DATABASE") ?? "blocwerk",
            Username = section["Postgres:Username"] ?? Environment.GetEnvironmentVariable("POSTGRES__USERNAME") ?? "postgres",
            Password = section["Postgres:Password"] ?? Environment.GetEnvironmentVariable("POSTGRES__PASSWORD") ?? string.Empty,
        };

        var importHost = section["DevImport:Postgres:Host"]
                         ?? Environment.GetEnvironmentVariable("BLOCWERK_DEV_IMPORT__HOST");
        if (!string.IsNullOrWhiteSpace(importHost))
        {
            DevImport = new PostgresSettings
            {
                Host = importHost,
                Port = int.TryParse(
                    section["DevImport:Postgres:Port"] ?? Environment.GetEnvironmentVariable("BLOCWERK_DEV_IMPORT__PORT"),
                    out var importPort)
                    ? importPort
                    : 5432,
                Database = section["DevImport:Postgres:Database"] ?? Environment.GetEnvironmentVariable("BLOCWERK_DEV_IMPORT__DATABASE") ?? "blocwerk",
                Username = section["DevImport:Postgres:Username"] ?? Environment.GetEnvironmentVariable("BLOCWERK_DEV_IMPORT__USERNAME") ?? "postgres",
                Password = section["DevImport:Postgres:Password"] ?? Environment.GetEnvironmentVariable("BLOCWERK_DEV_IMPORT__PASSWORD") ?? string.Empty,
            };
        }

        HoldDetection = new HoldDetectionSettings
        {
            ModelPath = section["HoldDetection:ModelPath"]
                        ?? Environment.GetEnvironmentVariable("HOLDDETECTION__MODELPATH")
                        ?? "models/climbingcrux.onnx",
        };

        BetaVideo = new BetaVideoSettings
        {
            StoragePath = section["BetaVideo:StoragePath"]
                          ?? Environment.GetEnvironmentVariable("BETAVIDEO__STORAGEPATH")
                          ?? "beta-videos",
            TargetVideoBitsPerSecond = ParseBytes(section["BetaVideo:TargetVideoBitsPerSecond"], "BETAVIDEO__TARGETVIDEOBITSPERSECOND", 3_000_000),
            MaxUploadBytes = ParseBytes(section["BetaVideo:MaxUploadBytes"], "BETAVIDEO__MAXUPLOADBYTES", 4L * 1024 * 1024 * 1024),
            MaxEncodeSeconds = (int)ParseBytes(section["BetaVideo:MaxEncodeSeconds"], "BETAVIDEO__MAXENCODESECONDS", 600),
            FfmpegPath = section["BetaVideo:FfmpegPath"] ?? Environment.GetEnvironmentVariable("BETAVIDEO__FFMPEGPATH") ?? "ffmpeg",
            FfprobePath = section["BetaVideo:FfprobePath"] ?? Environment.GetEnvironmentVariable("BETAVIDEO__FFPROBEPATH") ?? "ffprobe",
        };

        WallImage = new WallImageSettings
        {
            StoragePath = section["WallImage:StoragePath"]
                          ?? Environment.GetEnvironmentVariable("WALLIMAGE__STORAGEPATH")
                          ?? "wall-images",
        };

        Smtp = new SmtpSettings
        {
            Host = section["Smtp:Host"] ?? Environment.GetEnvironmentVariable("SMTP__HOST"),
            Port = int.TryParse(
                section["Smtp:Port"] ?? Environment.GetEnvironmentVariable("SMTP__PORT"),
                out var smtpPort)
                ? smtpPort
                : 587,
            Username = section["Smtp:Username"] ?? Environment.GetEnvironmentVariable("SMTP__USERNAME"),
            Password = section["Smtp:Password"] ?? Environment.GetEnvironmentVariable("SMTP__PASSWORD"),
            From = section["Smtp:From"] ?? Environment.GetEnvironmentVariable("SMTP__FROM"),
            FromName = section["Smtp:FromName"] ?? Environment.GetEnvironmentVariable("SMTP__FROMNAME") ?? "Blocwerk",
            Security = ParseSmtpSecurity(
                section["Smtp:Security"] ?? Environment.GetEnvironmentVariable("SMTP__SECURITY")),
        };

        GitHubOAuth = BindOAuthProvider(section, "GitHub", "https://github.com/login/oauth/authorize");
        GoogleOAuth = BindOAuthProvider(section, "Google", "https://accounts.google.com/o/oauth2/v2/auth");
        MicrosoftOAuth = BindOAuthProvider(section, "Microsoft", "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize");

        var admins = section.GetSection("AdminIdentifiers").Get<List<string>>();
        if (admins is not null)
        {
            AdminIdentifiers = admins;
        }
    }

    private static OAuthProviderSettings BindOAuthProvider(IConfigurationSection section, string name, string defaultUrl)
    {
        var prefix = $"{name}OAuth";
        var envPrefix = name.ToUpperInvariant();

        return new OAuthProviderSettings
        {
            Enabled = bool.TryParse(
                section[$"{prefix}:Enabled"] ?? Environment.GetEnvironmentVariable($"{envPrefix}__ENABLED"),
                out var enabled) && enabled,
            ClientId = section[$"{prefix}:ClientId"]
                       ?? Environment.GetEnvironmentVariable($"{envPrefix}__CLIENTID")
                       ?? string.Empty,
            ClientSecret = section[$"{prefix}:ClientSecret"]
                           ?? Environment.GetEnvironmentVariable($"{envPrefix}__CLIENTSECRET")
                           ?? string.Empty,
            OAuthUrl = section[$"{prefix}:OAuthUrl"]
                       ?? Environment.GetEnvironmentVariable($"{envPrefix}__OAUTHURL")
                       ?? defaultUrl,
        };
    }

    private static string GenerateRandomKey()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static SmtpSecurity ParseSmtpSecurity(string? value) =>
        Enum.TryParse<SmtpSecurity>(value, ignoreCase: true, out var security)
            ? security
            : SmtpSecurity.StartTls;

    private static long ParseBytes(string? sectionValue, string envName, long fallback) =>
        long.TryParse(sectionValue ?? Environment.GetEnvironmentVariable(envName), out var value) && value > 0
            ? value
            : fallback;
}

public class OAuthProviderSettings
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string OAuthUrl { get; set; } = string.Empty;
}

public class ServerSettings
{
    public string Url { get; set; } = "https://localhost:5001";

    public int Port { get; set; } = 5001;

    public string JwtIssuer => Url;
}

public class PostgresSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5051;

    public string Database { get; set; } = "blocwerk";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = string.Empty;

    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};SSL Mode=Prefer";
}

public class HoldDetectionSettings
{
    public string ModelPath { get; set; } = "models/climbingcrux.onnx";
}

/// <summary>
/// Beta clip storage and normalization. Clips are stored on disk (not the database) so large
/// uploads are possible. Every upload is stored verbatim first, then the background normalizer
/// re-encodes it to a universally playable H.264/AAC MP4 (already-web-safe clips are only remuxed).
/// </summary>
public class BetaVideoSettings
{
    public string StoragePath { get; set; } = "beta-videos";

    /// <summary>
    /// Target video bitrate (bits/s) for a full re-encode. ~3 Mbps keeps a typical short clip to a
    /// few MB while staying visually clean at the 720p cap; env <c>BETAVIDEO__TARGETVIDEOBITSPERSECOND</c>.
    /// The encoder caps the peak at 1.5x this, so file size scales with clip length, not the source.
    /// </summary>
    public long TargetVideoBitsPerSecond { get; set; } = 3_000_000;

    /// <summary>Hard upload ceiling; env <c>BETAVIDEO__MAXUPLOADBYTES</c>. The original is stored, then normalized.</summary>
    public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hard per-ffmpeg-invocation ceiling (seconds); env <c>BETAVIDEO__MAXENCODESECONDS</c>. A clip that
    /// makes ffmpeg hang would otherwise block the single normalizer worker forever (a poison pill that
    /// survives reboot via the stale-Processing reset). On timeout the process is killed and the clip is
    /// marked Failed. 0 or negative disables the cap (not recommended). Default 10 minutes.
    /// </summary>
    public int MaxEncodeSeconds { get; set; } = 600;

    /// <summary>The <see cref="MaxEncodeSeconds"/> ceiling as a <see cref="TimeSpan"/>; infinite when disabled.</summary>
    public TimeSpan EncodeTimeout =>
        MaxEncodeSeconds > 0 ? TimeSpan.FromSeconds(MaxEncodeSeconds) : Timeout.InfiniteTimeSpan;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";
}

/// <summary>
/// Wall image storage. Images pushed in by cameras or clients are stored on disk rather than the
/// database, the same way beta clips are (see <see cref="BetaVideoSettings"/>).
/// </summary>
public class WallImageSettings
{
    public string StoragePath { get; set; } = "wall-images";
}
