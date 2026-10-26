using System.Net.Sockets;
using System.Reflection;
using Ecnanib.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Migrations;
using Ecnanib.Logging;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

// FR-CFG-01/02/06: the one configuration file, from --config or ./ecnanib.json, is the only source. A bad file ends
// here with its reason on the console and exit code 1; no logger exists yet, so the message goes straight to stderr.
WebApplicationBuilder builder;
try
{
    builder = EcnanibHost.CreateBuilder(ConfigurationFile.ResolvePath(args));
}
catch (ConfigurationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// First: both log sinks (C-08) and, as the first hosted service, the database log writer, which therefore stops last
// and still writes the other services' shutdown logs (FR-LOG-04).
builder.Services.AddLogSinks(builder.Configuration);
builder.Services.AddEcnanibModules(builder.Configuration);

// Kestrel reads the Server section while the host is built, before any logger exists: an invalid Server section (a
// plain-http public URL that is not loopback, NFR-SEC-02; an unparseable trusted proxy) ends here, on stderr, with exit
// code 1 like any other invalid setting (FR-CFG-06). Every other section is validated inside MigrateDatabaseAsync below.
await using var app = TryBuild(builder);
if (app is null)
    return 1;

try
{
    // DB-02, NFR-ARCH-03: bring the schema up to date before anything else runs; every configured section is
    // validated first (FR-CFG-06). The cause was logged at Critical, so the process exits without a stack trace.
    await app.MigrateDatabaseAsync();
    // NFR-REL-04: exactly one process per database. The lock is taken once the schema is in place and before any hosted
    // service runs, held by the host for its lifetime and released when it stops; a second instance waits
    // Database:InstanceLockWaitSeconds for the previous one to let go, then exits 1 with the cause logged at Critical.
    await app.AcquireInstanceLockAsync();
    // FR-CFG-05: the key ring that signs sessions exists after the first start, not after the first request.
    app.EnsureSystemKeys();
}
catch (Exception ex) when (ex is OptionsValidationException or DatabaseMigrationException or InstanceLockException
                           || ex is AggregateException aggregate && aggregate.InnerExceptions.All(e => e is OptionsValidationException))
{
    return 1;
}

// NFR-SEC-01/02: forwarded headers from the trusted proxy, the error pages, https redirection and HSTS behind an https
// public URL, the security headers, authentication before antiforgery, and the endpoints. One method, so the tests run
// the same pipeline in memory; the explicit order replaces WebApplication's automatic insertion.
app.UseEcnanibPipeline();

// Resolved before RunAsync, which disposes the host on its way out, including when the server fails to start.
var endPoint = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ToEndPoint();
// FR-LOG-04: the application's own start and stop events, beside the framework's lifetime messages. Stopping fires
// before the hosted services stop, so the database log writer still has it when its turn comes.
var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Ecnanib {Version} started; listening on {EndPoint}", version, endPoint));
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Ecnanib is stopping"));
try
{
    await app.RunAsync();
}
catch (Exception ex) when (ex is IOException { InnerException: AddressInUseException } or SocketException)
{
    // The Server section names an endpoint this host cannot bind; the same clear exit as any other invalid setting.
    app.Logger.LogCritical("Cannot listen on {EndPoint}; the application will not start: {Reason}", endPoint, ex.Message);
    return 1;
}

return 0;

static WebApplication? TryBuild(WebApplicationBuilder builder)
{
    try
    {
        return builder.Build();
    }
    catch (OptionsValidationException ex)
    {
        foreach (var failure in ex.Failures)
            Console.Error.WriteLine(failure);
        return null;
    }
}
