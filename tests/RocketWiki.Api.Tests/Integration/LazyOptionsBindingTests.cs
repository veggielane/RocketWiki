using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RocketWiki.Api.Hosting;
using RocketWiki.Api.Identity;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The options families Program.cs used to read EAGERLY off the builder — <c>Instance:Id</c>
/// closed over by a dozen service factories and the DbContext options callback,
/// <c>Keycloak:*</c> composed into the bearer authority, <c>Database:MigrateOnStartup</c>
/// — are now resolved from the container. This class proves it the only way that
/// matters: configuration layered in through <c>ConfigureAppConfiguration</c>, which a
/// WebApplicationFactory applies AFTER top-level statements have run, is the
/// configuration the host runs with. An eager read would still see the default
/// (ProtectiveMarkingConfiguration's doc records exactly that failure for the selector
/// catalog).
///
/// <para>Mutation-tested: put <c>builder.Configuration["Instance:Id"] ?? "standalone"</c>
/// back at the top of Program.cs and construct InstanceIdentity from it, and the first
/// test fails with "standalone".</para>
/// </summary>
public sealed class LazyOptionsBindingTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public void InstanceId_IsResolvedFromTheContainersConfiguration_NotCapturedOffTheBuilder()
    {
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Instance:Id"] = "high-side" })));

        Assert.Equal("high-side", host.Services.GetRequiredService<InstanceIdentity>().LocalInstanceId);
        Assert.Equal("high-side", host.Services.GetRequiredService<IOptions<InstanceOptions>>().Value.Id);
    }

    [Fact]
    public void InstanceId_DefaultsToStandalone()
    {
        Assert.Equal(InstanceOptions.DefaultId, factory.Services.GetRequiredService<InstanceIdentity>().LocalInstanceId);
    }

    /// <summary>The bearer authority is derived inside the options-configure callback from
    /// IOptions&lt;KeycloakOptions&gt; and the connection string, at first use — so both
    /// arrive through the container and a late realm/authority is the one used.</summary>
    [Fact]
    public void KeycloakOptions_AreResolvedFromTheContainersConfiguration()
    {
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Keycloak:Realm"] = "late-realm",
                    ["Keycloak:Audience"] = "late-audience",
                    ["ConnectionStrings:keycloak"] = "http://keycloak.test:8080/",
                })));

        var keycloak = host.Services.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        Assert.Equal("late-realm", keycloak.Realm);
        Assert.Equal("late-audience", keycloak.Audience);

        var bearer = host.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal("http://keycloak.test:8080/realms/late-realm", bearer.Authority);
        Assert.Equal("late-audience", bearer.Audience);
    }

    /// <summary>The shared fixture turns migrations off through ConfigureAppConfiguration;
    /// that the host boots at all on SQLite is the proof the value is read late, and this
    /// says so explicitly.</summary>
    [Fact]
    public void DatabaseOptions_ReflectTheTestHostsMigrateOnStartupFalse()
    {
        Assert.False(factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup);
    }
}
