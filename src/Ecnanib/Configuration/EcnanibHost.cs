using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Ecnanib.Configuration;

/// <summary>
/// Builds the application host the way <see cref="WebApplication.CreateBuilder(string[])"/> builds every ASP.NET Core
/// host (FR-CFG-01, FR-CFG-02): <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c> from the content root
/// (the working directory), environment variables, the command line, Kestrel configured from the <c>Kestrel</c>
/// section, routing and host filtering. What the files contain is checked here (<see cref="ConfigurationFile.VerifySections"/>)
/// and a file that cannot be loaded is reported as a <see cref="ConfigurationException"/> for the console (FR-CFG-06).
/// Logging is not touched here: both sinks (C-08) are the logging module's, registered by <c>AddLogSinks</c> in
/// <c>Program.cs</c>.
/// </summary>
public static class EcnanibHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        WebApplicationBuilder builder;
        try
        {
            builder = WebApplication.CreateBuilder(args);
        }
        catch (InvalidDataException ex)
        {
            // A JSON file the provider could not parse: the framework names the file, the parser the position.
            throw new ConfigurationException(ConfigurationFile.DescribeLoadFailure(ex), ex);
        }

        ConfigurationFile.VerifySections(builder.Configuration);

        // Under `dotnet run` the scoped CSS bundle and the Blazor script live outside wwwroot and the build manifest
        // maps them. The default builder reads that manifest in Development only; reading it in every other environment
        // too lets `dotnet run --no-launch-profile` serve them. After publish the manifest is absent and this is a no-op.
        if (!builder.Environment.IsDevelopment())
            builder.WebHost.UseStaticWebAssets();

        // The container checks the default builder reserves for Development run everywhere.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        return builder;
    }

    /// <summary>
    /// Loads the <c>Kestrel</c> section before the database is touched (FR-CFG-06): an endpoint URL Kestrel cannot
    /// parse is logged at Critical and refused here, like any other invalid setting, instead of failing at bind time
    /// with a stack trace. Kestrel loads the section once, when the server starts; this call is that once.
    /// </summary>
    public static void ValidateListenEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.ConfigurationLoader?.Load();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever the loader refuses (an unparseable URL, an endpoint without one, a certificate it cannot read) is a setting the operator must fix.
            app.Logger.LogCritical("The Kestrel section is invalid; the application will not start: {Reason}", ex.Message);
            throw new ConfigurationException($"The Kestrel section is invalid: {ex.Message}", ex);
        }
    }
}
