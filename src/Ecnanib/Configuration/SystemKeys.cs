using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Ecnanib.Configuration;

/// <summary>
/// FR-CFG-05, A-01: the keys the system needs for itself are generated on first start and stored in the database.
/// Session tickets and antiforgery tokens are protected by the Data Protection key ring, which the database module
/// persists in <c>data_protection_keys</c>; this step makes the first start create it instead of the first request.
/// </summary>
public static class SystemKeys
{
    private const string Purpose = "Ecnanib.Configuration.SystemKeys";

    public static void EnsureSystemKeys(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var keys = host.Services.GetRequiredService<IKeyManager>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SystemKeys).FullName!);

        var before = keys.GetAllKeys().Count(key => !key.IsRevoked);
        // Protecting anything makes the key ring resolve its default key and create one when none is usable.
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect([1]);
        var after = keys.GetAllKeys().Count(key => !key.IsRevoked);

        if (after > before)
            logger.LogInformation("Generated the Data Protection key and stored it in the database (FR-CFG-05)");
        else
            logger.LogInformation("Data Protection key ring loaded from the database ({Count} keys)", after);
    }
}
