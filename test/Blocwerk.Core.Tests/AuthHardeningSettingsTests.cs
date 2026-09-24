using System.Net;
using Blocwerk.Core.Configuration;
using Blocwerk.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The API-key login allow-list and the trusted-proxy list: bound from configuration, empty by
/// default, and a malformed entry fails startup rather than quietly meaning something else.
/// </summary>
public class AuthHardeningSettingsTests
{
    [Fact]
    public void AllowList_IsEmptyByDefault()
    {
        var settings = new BlocwerkSettings(Config(new()));

        Assert.False(settings.Auth.ApiKeyLogin.Enabled);
        Assert.Empty(settings.Auth.ApiKeyLogin.AllowedUserIds);
        Assert.Empty(settings.Server.TrustedProxies);
    }

    [Fact]
    public void AllowList_BindsTheArrayForm()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var settings = new BlocwerkSettings(Config(new()
        {
            ["Blocwerk:Auth:ApiKeyLogin:AllowedUserIds:0"] = a.ToString(),
            ["Blocwerk:Auth:ApiKeyLogin:AllowedUserIds:1"] = b.ToString(),
        }));

        Assert.Equal(new HashSet<Guid> { a, b }, settings.Auth.ApiKeyLogin.AllowedUserIds);
    }

    [Fact]
    public void AllowList_AcceptsACommaSeparatedValue_AndRefusesJunk()
    {
        var a = Guid.NewGuid();

        var settings = new BlocwerkSettings(Config(new()
        {
            ["Blocwerk:Auth:ApiKeyLogin:AllowedUserIds"] = $" {a} ,",
        }));

        Assert.Equal(new HashSet<Guid> { a }, settings.Auth.ApiKeyLogin.AllowedUserIds);
        Assert.Throws<InvalidOperationException>(() => new BlocwerkSettings(Config(new()
        {
            ["Blocwerk:Auth:ApiKeyLogin:AllowedUserIds:0"] = "not-a-guid",
        })));
    }

    [Fact]
    public void TrustedProxies_EmptyTrustsEveryone_ListedEntriesAreTheOnlyOnesTrusted()
    {
        var open = new ForwardedHeadersOptions();
        TrustedProxies.Apply(open, []);
        Assert.Empty(open.KnownProxies);
        Assert.Empty(open.KnownIPNetworks);

        var settings = new BlocwerkSettings(Config(new()
        {
            ["Blocwerk:Server:TrustedProxies:0"] = "10.0.0.5",
            ["Blocwerk:Server:TrustedProxies:1"] = "172.18.0.0/16",
        }));
        var closed = new ForwardedHeadersOptions();
        TrustedProxies.Apply(closed, settings.Server.TrustedProxies);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), Assert.Single(closed.KnownProxies));
        Assert.Single(closed.KnownIPNetworks);
        Assert.True(closed.KnownIPNetworks[0].Contains(IPAddress.Parse("172.18.3.4")));
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Apply(new ForwardedHeadersOptions(), ["proxy.local"]));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
