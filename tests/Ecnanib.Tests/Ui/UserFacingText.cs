using System.Net;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Ecnanib.Tests.Ui;

/// <summary>Literal text in a component, which a translation would never reach.</summary>
internal sealed record HardcodedText(string Source, string Text)
{
    public override string ToString() => $"{Source}: \"{Text}\"";
}

/// <summary>
/// EXT-UI-02. Razor compiles to RenderTreeBuilder calls, so this reads those rather than the .razor
/// files, and it sees markup, attributes, lambdas and child content. It takes the assembly and root
/// namespace as parameters so that UserFacingTextGuardTests can prove it catches real text. See
/// docs/architecture.md for what it can't see.
/// </summary>
internal sealed partial class UserFacingText(ReflectionAssembly assembly, string rootNamespace)
{
    private const string Builder = "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";

    /// <summary>Parameters beyond these are mostly routes, classes and selectors.</summary>
    private static readonly HashSet<string> Labels = ["aria-label", "title", "alt", "placeholder"];

    private readonly string _prefix = rootNamespace + ".";

    /// <summary>How many text-carrying builder calls the last scan read, so it can't pass on nothing.</summary>
    public int Inspected { get; private set; }

    public List<HardcodedText> FindText()
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        var found = new List<HardcodedText>();
        Inspected = 0;

        foreach (var type in module.GetTypes().Where(type => type.FullName.StartsWith(_prefix, StringComparison.Ordinal)))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                found.AddRange(ScanMethod(method).Select(text => new HardcodedText(type.FullName, text)));
            }
        }

        return found;
    }

    private static bool IsText(string value) => value.Any(char.IsLetter);

    private List<string> ScanMethod(MethodDefinition method)
    {
        var found = new List<string>();

        // Everything since the last builder call, so that a ternary or a concatenation still counts.
        var literals = new List<string>();

        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldstr)
            {
                literals.Add((string)instruction.Operand);
            }
            else if (instruction.Operand is MethodReference { DeclaringType.FullName: Builder } call)
            {
                found.AddRange(ScanCall(call.Name, literals));
                literals.Clear();
            }
        }

        return found;
    }

    private IEnumerable<string> ScanCall(string method, List<string> literals)
    {
        switch (method)
        {
            case "AddMarkupContent":
                Inspected++;
                return literals.SelectMany(TextInMarkup);

            case "AddContent":
                Inspected++;
                return literals.Where(IsText);

            // The attribute's name is the first literal, and anything after it is its value.
            case "AddAttribute" or "AddComponentParameter" when literals.Count > 1 && Labels.Contains(literals[0]):
                return literals.Skip(1).Where(IsText);

            default:
                return [];
        }
    }

    private static IEnumerable<string> TextInMarkup(string markup)
    {
        foreach (var match in Label().Matches(markup).Where(match => IsText(WebUtility.HtmlDecode(match.Groups["value"].Value))))
        {
            yield return match.Groups["value"].Value;
        }

        var text = WebUtility.HtmlDecode(Tag().Replace(markup, " ")).Trim();

        if (IsText(text))
        {
            yield return text;
        }
    }

    [GeneratedRegex("\\s(?:aria-label|title|alt|placeholder)=\"(?<value>[^\"]*)\"")]
    private static partial Regex Label();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();
}
