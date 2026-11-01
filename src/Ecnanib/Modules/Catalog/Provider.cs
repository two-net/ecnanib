namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A supported AI provider (CON-13). Providers are data, not an enum (NFR-MNT-02). The name is the
/// key, so modules that can't depend on Catalog, such as Credentials, can hold it directly.
/// </summary>
internal sealed class Provider
{
    public required string Name { get; init; }

    public bool Enabled { get; set; } = true;
}
