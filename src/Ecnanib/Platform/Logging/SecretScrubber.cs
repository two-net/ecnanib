using Ecnanib.Platform.Contracts;

namespace Ecnanib.Platform.Logging;

/// <summary>The rules are read once, so a configured secret added later needs a restart, as the audit trail does.</summary>
internal sealed class SecretScrubber(IConfiguration configuration) : ISecretScrubber
{
    private readonly SecretRules _rules = SecretRules.From(configuration);

    public string Scrub(string text) => _rules.Scrub(text);
}
