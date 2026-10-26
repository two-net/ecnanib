namespace Ecnanib.Tests.Support;

/// <summary>Every rule of <see cref="AccessibilityChecks"/> fires on a defect and stays quiet on its fix, so the page tours prove something.</summary>
public class AccessibilityChecksTests
{
    private const string Shell = """<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Test</title><meta name="viewport" content="width=device-width, initial-scale=1.0"></head><body><a class="skip-link" href="#main">Skip to content</a><main id="main"><h1>Heading</h1>{0}</main></body></html>""";

    private static IReadOnlyList<AccessibilityViolation> Check(string body) => AccessibilityChecks.Check(Shell.Replace("{0}", body));

    [Fact]
    public void TheShellHasNoViolation() => Assert.Empty(Check(""));

    [Theory]
    [InlineData("meta-viewport", """<meta name="viewport" content="width=device-width, maximum-scale=1">""", "")]
    [InlineData("meta-refresh", """<meta http-equiv="refresh" content="60">""", "")]
    [InlineData("landmark-main-once", "<main></main>", "")]
    [InlineData("skip-link-target", """<a href="#nowhere">Nowhere</a>""", """<a href="#main">Top</a>""")]
    [InlineData("one-h1", "<h1>Second</h1>", "<h2>Second</h2>")]
    [InlineData("heading-order", "<h3>Skipped</h3>", "<h2>Next</h2><h3>Then</h3>")]
    [InlineData("duplicate-id", """<p id="x">a</p><p id="x">b</p>""", """<p id="x">a</p><p id="y">b</p>""")]
    [InlineData("control-name", """<input type="text" placeholder="Name">""", "<label>Name <input></label>")]
    [InlineData("control-name", """<select><option>a</option></select>""", """<label for="s">Side</label><select id="s"><option>a</option></select>""")]
    [InlineData("control-name", "<textarea></textarea>", """<textarea aria-label="Notes"></textarea>""")]
    [InlineData("button-name", "<button></button>", """<button aria-label="Dismiss">×</button>""")]
    [InlineData("button-name", "<details><summary></summary>x</details>", "<details><summary>More</summary>x</details>")]
    [InlineData("link-name", """<a href="/x"></a>""", """<a href="/x"><img src="x.png" alt="Home"></a>""")]
    [InlineData("aria-refs", """<div aria-describedby="nope">x</div>""", """<p id="d">desc</p><div aria-describedby="d">x</div>""")]
    [InlineData("aria-refs", """<p id="empty"></p><section aria-labelledby="empty">x</section>""", """<h2 id="t">Title</h2><section aria-labelledby="t">x</section>""")]
    [InlineData("aria-label-on-generic", """<div aria-label="Chart"></div>""", """<div role="img" aria-label="Chart"></div>""")]
    [InlineData("role-valid", """<div role="banana">x</div>""", """<div role="status">x</div>""")]
    [InlineData("aria-attr-values", """<p aria-live="loud">x</p>""", """<p aria-live="polite">x</p>""")]
    [InlineData("aria-live-nested", """<p aria-live="polite"><span role="status">x</span></p>""", """<p role="status" aria-live="polite">x</p>""")]
    [InlineData("aria-hidden-focus", """<div aria-hidden="true"><button>x</button></div>""", """<div aria-hidden="true"><a href="/x" tabindex="-1">x</a></div>""")]
    [InlineData("img-alt", """<img src="x.png">""", """<img src="x.png" alt="">""")]
    [InlineData("svg-name", "<svg></svg>", """<svg aria-hidden="true"></svg>""")]
    [InlineData("svg-name", """<svg role="img"></svg>""", """<svg role="img"><title>Council</title></svg>""")]
    [InlineData("table-name", "<table><tr><th>a</th></tr></table>", """<table><caption class="visually-hidden">Open orders</caption><tr><th>a</th></tr></table>""")]
    [InlineData("table-name", "<table><tr><th>a</th></tr></table>", """<h2 id="t">Trades</h2><table aria-labelledby="t"><tr><th>a</th></tr></table>""")]
    [InlineData("th-has-text", """<table aria-label="t"><tr><th scope="col"></th></tr></table>""", """<table aria-label="t"><tr><th scope="col"><span class="visually-hidden">Actions</span></th></tr></table>""")]
    [InlineData("fieldset-legend", """<fieldset><input aria-label="x"></fieldset>""", """<fieldset><legend>Markets</legend><input aria-label="x"></fieldset>""")]
    [InlineData("dialog-name", "<dialog><p>x</p></dialog>", """<dialog aria-labelledby="dt"><h2 id="dt">Connection</h2></dialog>""")]
    [InlineData("iframe-title", """<iframe src="x"></iframe>""", """<iframe src="x" title="Chart"></iframe>""")]
    [InlineData("no-positive-tabindex", """<button tabindex="2">x</button>""", """<button tabindex="0">x</button>""")]
    [InlineData("blazor-error-ui-dismiss", """<div id="blazor-error-ui">An error <a href="." class="reload">Reload</a> <span class="dismiss">x</span></div>""",
        """<div id="blazor-error-ui" role="alert">An error <a href="." class="reload">Reload</a> <button type="button" class="dismiss" aria-label="Dismiss">x</button></div>""")]
    public void ARuleFiresOnTheDefectAndNotOnTheFix(string rule, string failing, string passing)
    {
        Assert.Contains(Check(failing), v => v.Rule == rule);
        Assert.DoesNotContain(Check(passing), v => v.Rule == rule);
    }

    [Fact]
    public void TheDocumentRulesNeedTheirElements()
    {
        Assert.Contains(AccessibilityChecks.Check(Shell.Replace("{0}", "").Replace(" lang=\"en\"", "")), v => v.Rule == "html-lang");
        Assert.Contains(AccessibilityChecks.Check(Shell.Replace("{0}", "").Replace("<title>Test</title>", "")), v => v.Rule == "document-title");
    }

    /// <summary>A label names only its first labelable descendant: the day select is named, the time input beside it is not.</summary>
    [Fact]
    public void ALabelWrappingTwoControlsLeavesTheSecondUnnamed()
    {
        var violations = Check("""<label>From <select><option>Mon</option></select> <input type="time"></label>""");

        var violation = Assert.Single(violations, v => v.Rule == "control-name");
        Assert.Contains("type=\"time\"", violation.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlaceholderIsNotAName()
    {
        Assert.Null(AccessibilityChecks.AccessibleName(AccessibilityChecks.Parse(Shell.Replace("{0}", """<input placeholder="model id">""")).QuerySelector("input")!));
        Assert.Equal("Model id", AccessibilityChecks.AccessibleName(AccessibilityChecks.Parse(Shell.Replace("{0}", """<input placeholder="x" title="Model id">""")).QuerySelector("input")!));
    }

    [Fact]
    public void ViolationsReadAsOneLineEach()
    {
        var violation = Assert.Single(Check("""<section id="s"><button></button></section>"""));

        Assert.Equal("button-name", violation.Rule);
        Assert.Equal("section#s > button", violation.Where);
        Assert.Equal("button-name (4.1.2) at section#s > button: <button></button>", violation.ToString());
    }
}
