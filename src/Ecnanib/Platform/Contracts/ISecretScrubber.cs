namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Masks anything in a text that looks like a secret, with the same rules as the logs: by name, by
/// shape and by exact value from the configuration. It's a backstop for text that leaves the
/// application without passing a log sink, such as a notification (FR-NTF-03). It can't recognise a
/// secret with no marker, so never pass one. See Masking in docs/architecture.md.
/// </summary>
public interface ISecretScrubber
{
    string Scrub(string text);
}
