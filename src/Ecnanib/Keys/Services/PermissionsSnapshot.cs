using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Keys.Services;

/// <summary>The <c>exchange_keys.permissions_snapshot</c> column: the <see cref="ApiKeyPermissions"/> read at validation, as camel-case JSON.</summary>
internal static class PermissionsSnapshot
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    public static JsonElement Serialize(ApiKeyPermissions permissions) => JsonSerializer.SerializeToElement(permissions, Options).Clone();

    public static ApiKeyPermissions? Deserialize(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
            return null;
        try
        {
            return value.Deserialize<ApiKeyPermissions>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
