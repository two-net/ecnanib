namespace Ecnanib.Tests.Ui;

/// <summary>EXT-UI-02.</summary>
public sealed class UserFacingTextTests
{
    private readonly UserFacingText _scan = new(typeof(Program).Assembly, "Ecnanib.Components");

    [Fact]
    public void Components_have_no_literal_text() =>
        Assert.Empty(_scan.FindText().Select(text => text.ToString()));

    /// <summary>The fixtures are C#, so only this proves the scan still understands what Razor compiles to.</summary>
    [Fact]
    public void The_scan_reads_compiled_razor()
    {
        _scan.FindText();

        Assert.True(_scan.Inspected > 0);
    }
}
