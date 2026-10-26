namespace Ecnanib.Configuration;

/// <summary>
/// Host matching for the outbound allowlists (NFR-SEC-04): a host is under a domain when it is that domain or a
/// subdomain of it. Case and a trailing dot (a fully qualified name) do not matter; a look-alike such as
/// <c>evilbinance.com</c> or <c>binance.com.example</c> is not under <c>binance.com</c>.
/// </summary>
public static class HostNames
{
    public static bool IsUnder(string host, string domain)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(domain);
        var candidate = host.TrimEnd('.');
        var root = domain.TrimEnd('.');
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
               || candidate.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
    }
}
