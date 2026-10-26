using Ecnanib.Accounts;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Services;
using Ecnanib.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.DependencyInjection;

public class AddAccountsTests
{
    [Fact]
    public async Task ResolvesTheModuleWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = AccountsTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.NotNull(provider.GetRequiredService<IAccountService>());
        Assert.NotNull(provider.GetRequiredService<IFairUseLimits>());
        Assert.NotNull(provider.GetRequiredService<SignInThrottle>());
        Assert.NotNull(provider.GetRequiredService<SessionRevocations>());
        Assert.NotNull(provider.GetRequiredService<SessionValidator>());
        Assert.IsType<DatabaseSecurityStampSource>(provider.GetRequiredService<ISecurityStampSource>());
        Assert.IsType<Argon2idPasswordHasher>(provider.GetRequiredService<IPasswordHasher<User>>());
        Assert.NotNull(provider.GetRequiredService<IAuthorizationService>());

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, scheme?.Name);

        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<AccountAuthenticationStateProvider>(scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccountCookieEvents>());
        Assert.IsType<UserPreferencesResolver>(scope.ServiceProvider.GetRequiredService<IUserPreferencesResolver>());

        Assert.DoesNotContain(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.Accounts", StringComparison.Ordinal));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task TheModuleProviderWinsEvenWhenRazorComponentsAreRegisteredFirst()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString }).Build();
        var services = new ServiceCollection();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddServer(configuration);
        services.AddDatabase(configuration);
        services.AddAccounts(configuration);
        await using var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<AccountAuthenticationStateProvider>(scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
    }

    [Fact]
    public async Task CookieOptionsMatchTheDesign()
    {
        await using var provider = AccountsTestHost.BuildServices();

        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal("ecnanib.session", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.True(options.Cookie.IsEssential);
        Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/sign-in", options.LoginPath.Value);
        Assert.Equal("/sign-out", options.LogoutPath.Value);
        Assert.Equal("ReturnUrl", options.ReturnUrlParameter);
        Assert.Equal(typeof(AccountCookieEvents), options.EventsType);
    }

    [Fact]
    public async Task SessionLifetimeComesFromConfiguration()
    {
        await using var provider = AccountsTestHost.BuildServices(config: new() { ["Accounts:SessionDays"] = "7" });

        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.FromDays(7), options.ExpireTimeSpan);
    }

    [Theory]
    [InlineData(null, CookieSecurePolicy.SameAsRequest)]
    [InlineData("http://localhost:5207", CookieSecurePolicy.SameAsRequest)]
    [InlineData("https://ecnanib.example.com", CookieSecurePolicy.Always)]
    public async Task CookiesAreSecureWhenThePublicUrlIsHttps(string? publicUrl, CookieSecurePolicy expected)
    {
        var config = publicUrl is null ? null : new Dictionary<string, string?> { ["Server:PublicUrl"] = publicUrl };
        await using var provider = AccountsTestHost.BuildServices(config: config);

        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(expected, options.Cookie.SecurePolicy);
    }

    /// <summary>NFR-SEC-01: the antiforgery cookie follows the session cookie's rules and leaves the frame headers to the pipeline.</summary>
    [Theory]
    [InlineData("http://localhost:5207", CookieSecurePolicy.SameAsRequest)]
    [InlineData("https://ecnanib.example.com", CookieSecurePolicy.Always)]
    public async Task TheAntiforgeryCookieMatchesTheDesign(string publicUrl, CookieSecurePolicy expected)
    {
        await using var provider = AccountsTestHost.BuildServices(config: new() { ["Server:PublicUrl"] = publicUrl });

        var options = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        Assert.Equal("ecnanib.antiforgery", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.True(options.Cookie.IsEssential);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(expected, options.Cookie.SecurePolicy);
        Assert.True(options.SuppressXFrameOptionsHeader);
    }

    [Fact]
    public async Task DataProtectionUsesAStableApplicationName()
    {
        await using var provider = AccountsTestHost.BuildServices();

        Assert.Equal("Ecnanib", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }
}
