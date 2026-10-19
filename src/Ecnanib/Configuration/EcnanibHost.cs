namespace Ecnanib.Configuration;

/// <summary>
/// Builds the application host from the configuration file and nothing else (FR-CFG-01, FR-CFG-02). It starts from
/// <see cref="WebApplication.CreateEmptyBuilder"/>, which reads no environment variables, no <c>appsettings</c>
/// files, no user secrets and no command line, and adds back only what the application uses.
/// </summary>
public static class EcnanibHost
{
    public static WebApplicationBuilder CreateBuilder(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        ConfigurationFile.Load(builder.Configuration, configurationPath);

        // Plain HTTP on Server:BindAddress:Port (AddServer); TLS terminates at the reverse proxy.
        builder.WebHost.UseKestrelCore();
        // Under `dotnet run` the scoped CSS bundle and the Blazor script live outside wwwroot and the build manifest
        // maps them; after publish the manifest is absent and this is a no-op.
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRouting();

        // C-08: the console sink, with the per-category levels of the Logging section; the database sink arrives
        // with the logging issue.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection(ConfigurationFile.LoggingSection));
        builder.Logging.AddConsole();

        // There is no Development environment, so the container checks the default builder reserves for it run everywhere.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        return builder;
    }
}
