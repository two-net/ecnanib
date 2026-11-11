using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Ecnanib.Platform.Settings;

/// <summary>
/// Reads typed values from the parsed configuration file and collects every problem it finds, so
/// the application can report all wrong keys at once (NFR-CFG-4).
/// </summary>
/// <remarks>
/// Keys are paths with <c>:</c> between levels, such as <c>Email:Port</c>, and ignore case, as in
/// an appsettings file. A read that fails records a problem and returns an empty value, so reading
/// continues; the caller discards the result when any problem was recorded. Problems name the key
/// and the rule, never the value.
/// </remarks>
internal sealed class SettingsReader
{
    private const char Separator = ':';

    private enum Kind
    {
        Text,
        Number,
        Flag,
        Null,
        List,
        Object,
    }

    private readonly record struct Node(Kind Kind, string? Text);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _keysInFileOrder = [];

    // Keys the application asked for, with their parents. A key in the file that is not here is unknown.
    private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

    // Keys already reported with the wrong shape. Whatever they contain is not reported again.
    private readonly HashSet<string> _closed = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SettingsProblem> _problems = [];

    public SettingsReader(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            Add(parent: null, property.Name, property.Value);
        }
    }

    public IReadOnlyList<SettingsProblem> Problems => _problems;

    public static string Join(string parent, string child) => $"{parent}{Separator}{child}";

    public void Problem(string key, string rule)
    {
        var problem = new SettingsProblem(key, rule);
        if (!_problems.Contains(problem))
        {
            _problems.Add(problem);
        }
    }

    /// <summary>Required text that is not empty and is not a placeholder such as <c>&lt;the mail server&gt;</c>.</summary>
    public string Text(string key) =>
        TryScalar(key, Kind.Text, "must be text", out var text) && IsFilledIn(key, text) ? text : string.Empty;

    /// <summary>Text that may be left out or left empty.</summary>
    public string? OptionalText(string key)
    {
        if (IsAbsent(key) || IsEmptyText(key))
        {
            return null;
        }

        var text = Text(key);
        return text.Length == 0 ? null : text;
    }

    /// <summary>True when an optional text key holds something, valid or not.</summary>
    public bool IsSet(string key) => !IsAbsent(key) && !IsEmptyText(key);

    public Secret SecretText(string key) => new(Text(key));

    public Secret? OptionalSecret(string key) => OptionalText(key) is { } text ? new Secret(text) : null;

    /// <summary>A master encryption key: 32 random bytes in Base64, which is 256 bits (FR-KEY-6).</summary>
    public Secret MasterKey(string key)
    {
        var text = Text(key);
        if (text.Length == 0)
        {
            return new Secret(string.Empty);
        }

        Span<byte> decoded = stackalloc byte[64];
        if (!Convert.TryFromBase64String(text, decoded, out var length) || length != 32)
        {
            Problem(key, "must be 32 random bytes in Base64, which is a 256-bit key");
        }

        return new Secret(text);
    }

    public int WholeNumber(string key, int minimum = 1, int maximum = int.MaxValue)
    {
        var rule = maximum == int.MaxValue
            ? $"must be a whole number, {minimum} or more"
            : $"must be a whole number from {minimum} to {maximum}";

        if (!TryScalar(key, Kind.Number, rule, out var text))
        {
            return 0;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            || number < minimum
            || number > maximum)
        {
            Problem(key, rule);
            return 0;
        }

        return number;
    }

    public decimal Number(string key, decimal minimum)
    {
        var rule = $"must be a number, {Show(minimum)} or more";

        if (!TryDecimal(key, rule, out var number))
        {
            return 0;
        }

        if (number < minimum)
        {
            Problem(key, rule);
            return 0;
        }

        return number;
    }

    public bool Flag(string key) =>
        TryScalar(key, Kind.Flag, "must be true or false", out var text) && bool.Parse(text);

    /// <summary>An absolute http or https address.</summary>
    public Uri Url(string key)
    {
        var text = Text(key);
        if (text.Length > 0)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
            {
                return url;
            }

            Problem(key, "must be an absolute http or https address");
        }

        return new Uri("about:blank");
    }

    public string EmailAddress(string key)
    {
        var text = Text(key);
        if (text.Length > 0 && !LooksLikeEmailAddress(text))
        {
            Problem(key, "must be an email address");
            return string.Empty;
        }

        return text;
    }

    public IPAddress IpAddress(string key)
    {
        var text = Text(key);
        if (text.Length > 0)
        {
            if (IPAddress.TryParse(text, out var address))
            {
                return address;
            }

            Problem(key, "must be an IP address");
        }

        return IPAddress.None;
    }

    /// <summary>One of the names of <typeparamref name="T"/>, in any letter case.</summary>
    public T Choice<T>(string key)
        where T : struct, Enum
    {
        var text = Text(key);
        if (text.Length == 0)
        {
            return default;
        }

        foreach (var name in Enum.GetNames<T>())
        {
            if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<T>(name);
            }
        }

        Problem(key, $"must be one of: {string.Join(", ", Enum.GetNames<T>())}");
        return default;
    }

    /// <summary>Three numbers in the order minimum, default, maximum, each above and up to the given limits.</summary>
    public Bounded<decimal> Bounds(string key, decimal above, decimal upTo)
    {
        var rule = $"must be a number above {Show(above)} and at most {Show(upTo)}";

        return ThreeValues<decimal>(key, item =>
        {
            if (!TryDecimal(item, rule, out var number))
            {
                return null;
            }

            if (number <= above || number > upTo)
            {
                Problem(item, rule);
                return null;
            }

            return number;
        });
    }

    /// <summary>Three whole numbers in the order minimum, default, maximum, each within the given range.</summary>
    public Bounded<int> WholeBounds(string key, int minimum, int maximum)
    {
        return ThreeValues<int>(key, item =>
        {
            var before = _problems.Count;
            var number = WholeNumber(item, minimum, maximum);
            return _problems.Count == before ? number : null;
        });
    }

    /// <summary>
    /// The keys of the items of a required list, in order. Each item is then read like any other
    /// key. <paramref name="emptyRule"/>, when given, is the problem reported for an empty list.
    /// </summary>
    public IReadOnlyList<string> List(string key, string? emptyRule = null) =>
        [.. Container(key, Kind.List, "must be a list", emptyRule).Select(index => Join(key, index))];

    /// <summary>
    /// The names of the entries of a required object whose names are chosen in the file, such as
    /// the master keys. The key of an entry is <see cref="Join"/> of <paramref name="key"/> and
    /// its name. <paramref name="emptyRule"/>, when given, is the problem reported when the object
    /// has no entries.
    /// </summary>
    public IReadOnlyList<string> Map(string key, string? emptyRule = null) =>
        Container(key, Kind.Object, "must be an object", emptyRule);

    /// <summary>True when an optional object is present.</summary>
    public bool Has(string key)
    {
        if (IsAbsent(key))
        {
            return false;
        }

        if (_nodes[key].Kind == Kind.Object)
        {
            return true;
        }

        WrongShape(key, "must be an object");
        return false;
    }

    /// <summary>Reports a problem when the same item appears twice in a list, ignoring case.</summary>
    public void Distinct(string key, IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (items.Where(item => item.Length > 0).Any(item => !seen.Add(item)))
        {
            Problem(key, "must not list the same item twice");
        }
    }

    /// <summary>
    /// Reports every key in the file that the application did not ask for. Call it last. A key
    /// inside an unknown or wrongly shaped key is not reported again.
    /// </summary>
    public void ReportUnknownKeys()
    {
        foreach (var key in _keysInFileOrder)
        {
            if (_read.Contains(key))
            {
                continue;
            }

            var reportedWithParent = Parents(key).Any(parent =>
                _closed.Contains(parent) || (_nodes.ContainsKey(parent) && !_read.Contains(parent)));

            if (!reportedWithParent)
            {
                Problem(key, "is not a known setting");
            }
        }
    }

    private void Add(string? parent, string name, JsonElement element)
    {
        var key = parent is null ? name : Join(parent, name);

        var node = element.ValueKind switch
        {
            JsonValueKind.Object => new Node(Kind.Object, null),
            JsonValueKind.Array => new Node(Kind.List, null),
            JsonValueKind.String => new Node(Kind.Text, element.GetString()),
            JsonValueKind.Number => new Node(Kind.Number, element.GetRawText()),
            JsonValueKind.True => new Node(Kind.Flag, bool.TrueString),
            JsonValueKind.False => new Node(Kind.Flag, bool.FalseString),
            _ => new Node(Kind.Null, null),
        };

        if (!_nodes.TryAdd(key, node))
        {
            // The first one is kept. Marking the key as read keeps it out of the unknown keys.
            _read.Add(key);
            Problem(key, "appears more than once");
            return;
        }

        _keysInFileOrder.Add(key);
        if (parent is not null)
        {
            _children[parent].Add(name);
        }

        switch (node.Kind)
        {
            case Kind.Object:
                _children[key] = [];
                foreach (var property in element.EnumerateObject())
                {
                    Add(key, property.Name, property.Value);
                }

                break;

            case Kind.List:
                _children[key] = [];
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Add(key, index.ToString(CultureInfo.InvariantCulture), item);
                    index++;
                }

                break;
        }
    }

    private bool TryScalar(string key, Kind expected, string rule, out string text)
    {
        text = string.Empty;

        if (IsAbsent(key))
        {
            ReportMissing(key);
            return false;
        }

        var node = _nodes[key];
        if (node.Kind != expected)
        {
            WrongShape(key, rule);
            return false;
        }

        text = node.Text ?? string.Empty;
        return true;
    }

    private bool TryDecimal(string key, string rule, out decimal number)
    {
        number = 0;

        if (!TryScalar(key, Kind.Number, rule, out var text))
        {
            return false;
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            Problem(key, rule);
            return false;
        }

        return true;
    }

    private bool IsFilledIn(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Problem(key, "must not be empty");
            return false;
        }

        if (text.StartsWith('<') && text.EndsWith('>'))
        {
            Problem(key, "still holds a placeholder");
            return false;
        }

        return true;
    }

    private IReadOnlyList<string> Container(string key, Kind expected, string rule, string? emptyRule)
    {
        if (IsAbsent(key))
        {
            ReportMissing(key);
            return [];
        }

        if (_nodes[key].Kind != expected)
        {
            WrongShape(key, rule);
            return [];
        }

        var names = _children[key];
        if (names.Count == 0 && emptyRule is not null)
        {
            Problem(key, emptyRule);
        }

        return names;
    }

    private Bounded<T> ThreeValues<T>(string key, Func<string, T?> read)
        where T : struct, IComparable<T>
    {
        var items = List(key);
        if (!_nodes.TryGetValue(key, out var node) || node.Kind != Kind.List)
        {
            return default;
        }

        if (items.Count != 3)
        {
            Problem(key, "must be a list of three numbers: minimum, default, maximum");
            _closed.Add(key);
            return default;
        }

        var minimum = read(items[0]);
        var @default = read(items[1]);
        var maximum = read(items[2]);
        if (minimum is null || @default is null || maximum is null)
        {
            return default;
        }

        if (minimum.Value.CompareTo(@default.Value) > 0 || @default.Value.CompareTo(maximum.Value) > 0)
        {
            Problem(key, "must be in the order minimum, default, maximum");
        }

        return new Bounded<T>(minimum.Value, @default.Value, maximum.Value);
    }

    /// <summary>True when the key is not in the file or is null. Either way it now counts as asked for.</summary>
    private bool IsAbsent(string key)
    {
        _read.Add(key);
        foreach (var parent in Parents(key))
        {
            _read.Add(parent);
        }

        return !_nodes.TryGetValue(key, out var node) || node.Kind == Kind.Null;
    }

    private bool IsEmptyText(string key) =>
        _nodes.TryGetValue(key, out var node) && node is { Kind: Kind.Text, Text: "" };

    /// <summary>Reports the highest missing level once, so a missing section is one problem, not one per key.</summary>
    private void ReportMissing(string key)
    {
        foreach (var level in Parents(key).Reverse().Append(key))
        {
            if (!_nodes.TryGetValue(level, out var node) || node.Kind == Kind.Null)
            {
                Problem(level, "is required");
                return;
            }

            if (level != key && node.Kind is not (Kind.Object or Kind.List))
            {
                Problem(level, "must be an object");
                return;
            }
        }
    }

    private void WrongShape(string key, string rule)
    {
        Problem(key, rule);
        _closed.Add(key);
    }

    /// <summary>The keys above a key, nearest first: for <c>A:B:C</c>, <c>A:B</c> then <c>A</c>.</summary>
    private static IEnumerable<string> Parents(string key)
    {
        for (var end = key.LastIndexOf(Separator); end > 0; end = key.LastIndexOf(Separator, end - 1))
        {
            yield return key[..end];
        }
    }

    private static bool LooksLikeEmailAddress(string text)
    {
        var at = text.IndexOf('@');
        return at > 0
            && at == text.LastIndexOf('@')
            && at < text.Length - 1
            && !text.Any(char.IsWhiteSpace);
    }

    private static string Show(decimal number) => number.ToString(CultureInfo.InvariantCulture);
}
