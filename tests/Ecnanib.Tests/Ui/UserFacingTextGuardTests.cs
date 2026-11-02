namespace Ecnanib.Tests.Ui;

/// <summary>
/// Tests the scan against deliberately broken fixtures. It's the only automated guard on EXT-UI-02, so
/// a rule that silently stopped matching would be invisible without this.
/// </summary>
public sealed class UserFacingTextGuardTests
{
    private static readonly List<HardcodedText> Found =
        new UserFacingText(typeof(UserFacingTextGuardTests).Assembly, "Ecnanib.Tests.Ui.Fixtures").FindText();

    [Theory]
    [InlineData("TextInMarkup", "Hard-coded heading")]
    [InlineData("TextAfterAnEntity", "Prices & charts")]
    [InlineData("TextInContent", "Hard-coded content")]
    [InlineData("TextInChildContent", "Hard-coded title")]
    [InlineData("TextInBothBranches", "Yes")]
    [InlineData("TextInBothBranches", "No")]
    [InlineData("TextInInterpolation", "Hello ")]
    [InlineData("LabelInMarkup", "Close menu")]
    [InlineData("LabelAttribute", "Close menu")]
    [InlineData("LabelInATernary", "Open menu")]
    [InlineData("LabelParameter", "Close menu")]
    public void Literal_text_is_found(string fixture, string text) =>
        Assert.Contains(Found, found => found.Source.Contains(fixture, StringComparison.Ordinal) && found.Text.Contains(text, StringComparison.Ordinal));

    [Theory]
    [InlineData("MarkupWithoutText")]
    [InlineData("DoctypeAndEntities")]
    [InlineData("TextFromAResource")]
    [InlineData("LabelFromAResource")]
    [InlineData("OrdinaryAttribute")]
    [InlineData("OrdinaryParameter")]
    public void Anything_else_passes(string fixture) =>
        Assert.DoesNotContain(Found, found => found.Source.Contains(fixture, StringComparison.Ordinal));
}
