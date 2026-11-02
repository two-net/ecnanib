namespace Ecnanib.Tests.Identity;

/// <summary>
/// Addresses that got past validation at some point and failed further down: in PostgreSQL, which
/// rejects NUL and enforces the column length, or in string.Normalize, which throws on some characters.
/// Over HTTP that's a 500 and a database log row that a visitor chose to write.
/// </summary>
public static class MalformedAddresses
{
    /// <summary>What a form can deliver.</summary>
    public static TheoryData<string, string> Posted => new()
    {
        { "empty", string.Empty },
        { "blank", "   " },
        { "control character", "a\u0001b@example.test" },
        { "noncharacter", "\uFFFE" },
        { "noncharacter in an address", "\uFFFE@example.test" },
        { "too long", new string('a', 300) + "@example.test" },

        // Fits the column until NFC expands each of these to two characters.
        { "expands past the limit", new string('\u0958', 240) + "@a.co" },
    };

    /// <summary>
    /// What only a caller of the contract can pass. The form reader refuses NUL, and URL encoding can't
    /// carry a lone surrogate, but neither should depend on the caller being a form.
    /// </summary>
    public static TheoryData<string, string> All
    {
        get
        {
            var all = Posted;

            all.Add("NUL", "\0");
            all.Add("NUL in an address", "a\0b@example.test");
            all.Add("lone surrogate", "\uD800@example.test");

            return all;
        }
    }
}
