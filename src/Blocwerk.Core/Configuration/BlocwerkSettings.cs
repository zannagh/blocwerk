using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

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

    public HoldDetectionSettings HoldDetection { get; private set; } = new();

    public List<string> AdminIdentifiers { get; private set; } = [];

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
                : 5432,
            Database = section["Postgres:Database"] ?? Environment.GetEnvironmentVariable("POSTGRES__DATABASE") ?? "blocwerk",
            Username = section["Postgres:Username"] ?? Environment.GetEnvironmentVariable("POSTGRES__USERNAME") ?? "postgres",
            Password = section["Postgres:Password"] ?? Environment.GetEnvironmentVariable("POSTGRES__PASSWORD") ?? string.Empty,
        };

        HoldDetection = new HoldDetectionSettings
        {
            ModelPath = section["HoldDetection:ModelPath"]
                        ?? Environment.GetEnvironmentVariable("HOLDDETECTION__MODELPATH")
                        ?? "models/climbingcrux.onnx",
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

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "blocwerk";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = string.Empty;

    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password}";
}

public class HoldDetectionSettings
{
    public string ModelPath { get; set; } = "models/climbingcrux.onnx";
}
