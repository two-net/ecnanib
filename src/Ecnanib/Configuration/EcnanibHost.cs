namespace Ecnanib.Configuration;

/// <summary>
/// Builds the application host from the configuration file and nothing else (FR-CFG-01, FR-CFG-02). It starts from
/// <see cref="WebApplication.CreateEmptyBuilder"/>, which reads no environment variables, no <c>appsettings</c>
/// files, no user secrets and no command line, and adds back only what the application uses. Logging is not added
/// here: both sinks (C-08) are the logging module's, registered by <c>AddLogSinks</c> in <c>Program.cs</c>.
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

        // There is no Development environment, so the container checks the default builder reserves for it run everywhere.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        return builder;
    }
}
