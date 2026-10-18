using System.Text.Json;

namespace Ecnanib.Database.Entities;

/// <summary>
/// Safe defaults for <c>jsonb</c> columns. A default <see cref="JsonElement"/> is <c>Undefined</c> and cannot be
/// written; store these or a <see cref="JsonElement.Clone"/> of an element whose document outlives the entity.
/// </summary>
public static class JsonValues
{
    public static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    public static JsonElement EmptyArray { get; } = JsonDocument.Parse("[]").RootElement.Clone();
}
