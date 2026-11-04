namespace Ecnanib.Platform.Email;

/// <summary>
/// Says at startup whether email is on, and where it goes. The host builds every hosted service before
/// it starts any, and this one takes <see cref="EmailSettings"/>, so an invalid setting stops startup
/// before any migration runs (CFG-03).
/// </summary>
internal sealed class EmailStartup(EmailSettings settings, ILogger<EmailStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Server is { } server)
        {
            logger.LogInformation(
                "Email is on, through {SmtpHost}:{SmtpPort} with TLS {UseTls}",
                server.Host,
                server.Port,
                server.UseTls ? "required" : "off");
        }
        else
        {
            logger.LogInformation("Email is off, so addresses aren't verified and administrators issue password resets");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
