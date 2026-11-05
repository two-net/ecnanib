namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Says at startup which egress addresses users are told to whitelist, and how often keys are checked again.
/// The host builds every hosted service before it starts any, and this one takes
/// <see cref="CredentialSettings"/>, so an invalid setting stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class CredentialsStartup(CredentialSettings settings, ILogger<CredentialsStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.EgressIpAddresses.Count == 0)
        {
            logger.LogInformation(
                "No egress addresses are configured in {Setting}, so users aren't told what to whitelist on their Binance keys",
                CredentialSettings.EgressIpAddressesKey);
        }
        else
        {
            logger.LogInformation(
                "Users are told to whitelist {EgressIpAddresses} on their Binance keys",
                string.Join(", ", settings.EgressIpAddresses));
        }

        logger.LogInformation("Every key is checked with its provider again every {RevalidationHours} hours", settings.RevalidationInterval.TotalHours);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
