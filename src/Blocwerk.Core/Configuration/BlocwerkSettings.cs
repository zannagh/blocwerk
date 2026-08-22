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
            StoreAsIsMaxBytes = ParseBytes(section["BetaVideo:StoreAsIsMaxBytes"], "BETAVIDEO__STOREASISMAXBYTES", 800L * 1024 * 1024),
            TargetBytes = ParseBytes(section["BetaVideo:TargetBytes"], "BETAVIDEO__TARGETBYTES", 500L * 1024 * 1024),
            MaxUploadBytes = ParseBytes(section["BetaVideo:MaxUploadBytes"], "BETAVIDEO__MAXUPLOADBYTES", 4L * 1024 * 1024 * 1024),
            FfmpegPath = section["BetaVideo:FfmpegPath"] ?? Environment.GetEnvironmentVariable("BETAVIDEO__FFMPEGPATH") ?? "ffmpeg",
            FfprobePath = section["BetaVideo:FfprobePath"] ?? Environment.GetEnvironmentVariable("BETAVIDEO__FFPROBEPATH") ?? "ffprobe",
        };

        WallImage = new WallImageSettings
        {
            StoragePath = section["WallImage:StoragePath"]
                          ?? Environment.GetEnvironmentVariable("WALLIMAGE__STORAGEPATH")
                          ?? "wall-images",
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
/// Beta clip storage. Clips are stored on disk (not the database) so large uploads are possible.
/// Anything up to <see cref="StoreAsIsMaxBytes"/> is stored verbatim; larger clips are re-encoded
/// with ffmpeg down toward <see cref="TargetBytes"/>. <see cref="MaxUploadBytes"/> is a hard ceiling.
/// </summary>
public class BetaVideoSettings
{
    public string StoragePath { get; set; } = "beta-videos";

    public long StoreAsIsMaxBytes { get; set; } = 800L * 1024 * 1024;

    public long TargetBytes { get; set; } = 500L * 1024 * 1024;

    public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

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
