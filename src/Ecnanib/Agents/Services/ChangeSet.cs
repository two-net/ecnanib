using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Services;

/// <summary>
/// Collects the field-by-field differences of one edit as <see cref="AgentChange"/> rows (FR-AGT-07): one row per
/// changed field with the old and new value as JSON. Nothing is written for a field that did not change.
/// </summary>
internal sealed class ChangeSet
{
    public const string StateField = "state";

    public const string CreatedField = "created";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Guid _agentId;
    private readonly DateTimeOffset _at;
    private readonly List<AgentChange> _entries = [];

    public ChangeSet(Guid agentId, DateTimeOffset at)
    {
        _agentId = agentId;
        _at = at;
    }

    public IReadOnlyList<AgentChange> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>Records the change when the values differ; equality is by serialized JSON so decimals and enums compare by value.</summary>
    public bool Track<T>(string field, T oldValue, T newValue)
    {
        var before = Serialize(oldValue);
        var after = Serialize(newValue);
        if (before.GetRawText() == after.GetRawText())
            return false;
        _entries.Add(new AgentChange { AgentId = _agentId, Timestamp = _at, Field = field, OldValue = before, NewValue = after });
        return true;
    }

    public bool TrackJson(string field, JsonElement? oldValue, JsonElement? newValue)
    {
        var before = oldValue?.ValueKind is null or JsonValueKind.Undefined ? "null" : oldValue.Value.GetRawText();
        var after = newValue?.ValueKind is null or JsonValueKind.Undefined ? "null" : newValue.Value.GetRawText();
        if (before == after)
            return false;
        _entries.Add(new AgentChange { AgentId = _agentId, Timestamp = _at, Field = field, OldValue = Clone(oldValue), NewValue = Clone(newValue) });
        return true;
    }

    public void Note(string field, object? detail) =>
        _entries.Add(new AgentChange { AgentId = _agentId, Timestamp = _at, Field = field, OldValue = null, NewValue = Serialize(detail) });

    /// <summary>A lifecycle transition, so the history shows when and why an agent changed state.</summary>
    public static AgentChange State(Guid agentId, DateTimeOffset at, AgentState from, AgentState to, string? reason) => new()
    {
        AgentId = agentId,
        Timestamp = at,
        Field = StateField,
        OldValue = Serialize(from),
        NewValue = Serialize(new { state = to, reason }),
    };

    /// <summary>Decimals are normalised first so 1.0 and 1 are the same value, not a change.</summary>
    private static JsonElement Serialize<T>(T value) => value switch
    {
        decimal number => JsonSerializer.SerializeToElement(number / 1.0000000000000000000000000000m, Json),
        _ => JsonSerializer.SerializeToElement(value, Json),
    };

    private static JsonElement? Clone(JsonElement? value) =>
        value is { ValueKind: not JsonValueKind.Undefined } element ? element.Clone() : null;
}
