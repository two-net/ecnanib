using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.WebUi.Layout;
using Microsoft.AspNetCore.Components;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ecnanib.Tests.Legal;

/// <summary>A text of the application that names a company behind a provider, or says what would have someone stand behind the service.</summary>
/// <param name="Type">The type that holds the text.</param>
/// <param name="Literal">The text, as the type holds it.</param>
/// <param name="Word">What of it the rule looks for.</param>
internal sealed record FoundWord(string Type, string Literal, string Word)
{
    public override string ToString() => $"{Type}: \"{Literal}\" says \"{Word}\"";
}

/// <summary>An element that shows a picture, where a type writes one to a page.</summary>
/// <param name="Type">The type that writes it.</param>
/// <param name="Element">The element.</param>
internal sealed record FoundPicture(string Type, string Element)
{
    public override string ToString() => $"{Type} writes <{Element}>";
}

/// <summary>What the types of an assembly hold, as the rule reads them.</summary>
/// <param name="Words">The texts that the rule looks for and that are not listed, in order.</param>
/// <param name="Pictures">The elements that show a picture, written by a type that is not listed to draw.</param>
/// <param name="Listed">The listed texts that a type holds.</param>
/// <param name="Drawing">The types that are listed to draw, and do.</param>
internal sealed record ReadBrands(
    IReadOnlyList<FoundWord> Words, IReadOnlyList<FoundPicture> Pictures, IReadOnlySet<string> Listed, IReadOnlySet<string> Drawing);

/// <summary>
/// How the application names the exchange and the AI providers, in one place (NFR-LEG-4): a
/// name is used only to say which one it is, nothing suggests that any of them stands behind
/// the service, and an official logo is used only where the terms of its brand allow it, which
/// is nowhere: the application shows no picture of anyone. This file is the source of truth:
/// <c>docs/legal.md</c> explains the rule, and <see cref="BrandRuleTests"/> holds the
/// application to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The compiled application is read, and no page is asked for.</b> A word that a text class,
/// a notification, an email, or a prompt holds is a literal in what was compiled of it, and so
/// is the name of an element that a page writes. So the rule reads every type of the
/// application, with the parts that the compiler made of it, and finds a word wherever it is,
/// whether or not a test reaches the page that shows it.
/// </para>
/// <para>
/// <b>What the rule does not read.</b> What the database holds, such as the name of a provider,
/// which the model catalog seeds from a literal that is read. What the configuration file
/// holds, such as the name of the service and the addresses of the APIs. And a sentence that
/// says the same in other words: the rule looks for words, and a person reads the texts. A
/// change to <see cref="Says"/> or to a list here is a decision, made where it shows.
/// </para>
/// </remarks>
internal static partial class BrandRules
{
    private const string Builder = "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";

    /// <summary>
    /// The companies behind the providers. A provider is named by what its models are called,
    /// "Claude", "Gemini", "GPT", and "Grok", as the specification names them, and the
    /// application says nothing of a company: that a company is named is what suggests that it
    /// has a part in the service.
    /// </summary>
    public static readonly IReadOnlyList<string> Companies = ["Anthropic", "Google", "OpenAI", "xAI"];

    /// <summary>
    /// What a mark of someone is called. No text of the application speaks of one: there is
    /// none to speak of.
    /// </summary>
    public static readonly IReadOnlyList<string> Marks = ["logo", "logos"];

    /// <summary>
    /// What would have the exchange or a provider stand behind the service, or the service be
    /// theirs, each as the word or the start of the words that say so.
    /// </summary>
    public static readonly IReadOnlyList<string> StandingBehind =
    [
        "partner", "endors", "sponsor", "affiliat", "official", "certified",
        "powered by", "approved by", "trusted by", "in association with",
    ];

    /// <summary>
    /// The texts that hold one of those words and name nobody, or say that nobody stands behind
    /// the service, each as the application holds it, with why. A text that is not listed is
    /// found.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TextsThatAreListed = new Dictionary<string, string>
    {
        ["anthropic-version"] =
            "The name of a header that the API of Claude asks for with every call. It is sent to that API, and no page shows it.",

        ["googleSearch"] =
            "The name that the API of Gemini has for its live search, in a call that asks for it. It is sent to that API, and no page shows it.",

        [Screens.Independence] =
            "The sentence at the foot of every page. It uses the words to say that there is nothing of the kind. " +
            "LayoutPageTests holds its words.",
    };

    /// <summary>
    /// The elements that show a picture. A logo is a picture, so a page that writes none of
    /// these shows no logo.
    /// </summary>
    public static readonly IReadOnlyList<string> PictureElements =
        ["img", "picture", "object", "embed", "canvas", "video", "audio", "iframe", "svg"];

    /// <summary>
    /// The components that draw, each with what it draws: a drawing of the application's own,
    /// made of what the application computes, and a mark of nobody. Only <c>svg</c> is theirs
    /// to write.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ComponentsThatDraw = new Dictionary<string, string>
    {
        ["Ecnanib.WebUi.Pages.Account.AuthenticatorQrCode"] =
            "The QR code that an authenticator app scans, drawn from the key of the user as black and white squares.",

        ["Ecnanib.WebUi.Pages.Agents.AgentEquityCurve"] =
            "The equity of an agent over time, drawn as a line from its own figures.",
    };

    /// <summary>
    /// What the application serves beside its pages, by the ending of the file, each with what
    /// it is. A picture and a font are files of another ending, so none is served.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ServedFiles = new Dictionary<string, string>
    {
        [".css"] = "A stylesheet: the one of the pages, and the one of the styles that are written beside a component.",
        [".js"] = "A script: the ones of the application, and the ones of the framework that it is built with.",
    };

    /// <summary>The types of the application, as the rule reads them.</summary>
    public static ReadBrands Application() =>
        Read(
            typeof(Program).Assembly,
            "Ecnanib",
            new HashSet<string>(TextsThatAreListed.Keys, StringComparer.Ordinal),
            new HashSet<string>(ComponentsThatDraw.Keys, StringComparer.Ordinal),
            withoutNamespace: true);

    /// <summary>The types of a namespace of an assembly, or of one under it, as the rule reads them.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="namespace">The namespace of the types.</param>
    /// <param name="listed">The texts that are listed.</param>
    /// <param name="drawing">The types that are listed to draw, by their full names.</param>
    /// <param name="withoutNamespace">Whether the types that are in no namespace are read as well: the one that starts the application is.</param>
    public static ReadBrands Read(
        Assembly assembly, string @namespace, IReadOnlySet<string> listed, IReadOnlySet<string> drawing, bool withoutNamespace = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(drawing);

        var resolver = new DefaultAssemblyResolver();

        foreach (var folder in new[] { assembly, typeof(object).Assembly, typeof(IComponent).Assembly }
                     .Select(known => Path.GetDirectoryName(known.Location))
                     .Where(folder => !string.IsNullOrEmpty(folder))
                     .Distinct())
        {
            resolver.AddSearchDirectory(folder);
        }

        using var read = AssemblyDefinition.ReadAssembly(assembly.Location, new ReaderParameters { AssemblyResolver = resolver });

        var words = new List<FoundWord>();
        var pictures = new List<FoundPicture>();
        var listedAndThere = new HashSet<string>(StringComparer.Ordinal);
        var drawingAndThere = new HashSet<string>(StringComparer.Ordinal);

        var types = read.MainModule.Types
            .Where(type => IsWithin(type.Namespace, @namespace) || (withoutNamespace && IsOfTheCode(type)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            foreach (var part in WithNested(type))
            {
                // A constant is copied into whatever uses it, and stands here even when nothing does.
                foreach (var constant in part.Fields.Where(field => field.HasConstant).Select(field => field.Constant).OfType<string>())
                {
                    Hold(type.FullName, constant);
                }

                foreach (var method in part.Methods.Where(method => method.HasBody))
                {
                    var instructions = method.Body.Instructions;

                    for (var index = 0; index < instructions.Count; index++)
                    {
                        if (instructions[index] is not { OpCode.Code: Code.Ldstr, Operand: string literal })
                        {
                            continue;
                        }

                        Hold(type.FullName, literal);

                        // The name of an element that is opened, or markup that the page holds as it is.
                        var elements = index + 1 < instructions.Count && OpensElement(instructions[index + 1])
                            ? [literal]
                            : Element().Matches(literal).Select(element => element.Groups[1].Value);

                        foreach (var element in elements.Select(element => element.ToLowerInvariant()).Where(PictureElements.Contains))
                        {
                            if (element == "svg" && drawing.Contains(type.FullName))
                            {
                                drawingAndThere.Add(type.FullName);
                            }
                            else
                            {
                                pictures.Add(new FoundPicture(type.FullName, element));
                            }
                        }
                    }
                }
            }
        }

        return new ReadBrands(words, pictures, listedAndThere, drawingAndThere);

        void Hold(string type, string literal)
        {
            if (Says().Match(literal) is not { Success: true } said)
            {
                return;
            }

            if (listed.Contains(literal))
            {
                listedAndThere.Add(literal);
            }
            else
            {
                words.Add(new FoundWord(type, Shown(literal), said.Value));
            }
        }
    }

    /// <summary>
    /// The files that the application serves which are of no listed kind, from the addresses
    /// that it serves a file at. A compressed copy is of the kind of what it is a copy of.
    /// </summary>
    /// <param name="addresses">The addresses.</param>
    public static IReadOnlyList<string> OfNoListedKind(IEnumerable<string> addresses) =>
    [
        .. addresses
            .Where(address => !ServedFiles.ContainsKey(Path.GetExtension(Uncompressed(address)).ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The addresses that the application serves a file at, as the build wrote them down beside
    /// what was built: the files of <c>wwwroot</c>, the styles that are written beside a
    /// component, and the scripts of the framework.
    /// </summary>
    public static IReadOnlyList<string> ServedAddresses()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.staticwebassets.endpoints.json");

        using var read = JsonDocument.Parse(File.ReadAllText(manifest));

        return [.. read.RootElement.GetProperty("Endpoints").EnumerateArray().Select(endpoint => endpoint.GetProperty("Route").GetString()!)];
    }

    /// <summary>
    /// Whether a stylesheet loads something from an address: a picture, a font, or another
    /// stylesheet. What it says about itself, between the signs of a comment, loads nothing.
    /// </summary>
    /// <param name="styles">The stylesheet.</param>
    public static bool Loads(string styles) => Load().IsMatch(Comment().Replace(styles, string.Empty));

    /// <summary>
    /// What the rule looks for in a text: a company behind a provider, a logo, or what would
    /// have someone stand behind the service. A word counts where it begins, so that
    /// "unofficial" and the "LogO" of a name of the code are left alone, and the short ones
    /// count only as whole words: "logo" and "xAI" are in "logout" and in other words.
    /// </summary>
    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?i:(?:xai|logos?)(?![A-Za-z0-9])|anthropic|google|openai|partner|endors|sponsor|affiliat|official|certified" +
        "|powered by|approved by|trusted by|in association with)")]
    public static partial Regex Says();

    private static bool OpensElement(Instruction instruction) =>
        instruction is { OpCode.Code: Code.Call or Code.Callvirt, Operand: MethodReference { Name: "OpenElement" } method }
        && method.DeclaringType.FullName == Builder;

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type) =>
        type.NestedTypes.SelectMany(WithNested).Prepend(type);

    private static bool IsWithin(string? name, string @namespace) =>
        name is not null && (name == @namespace || name.StartsWith(@namespace + ".", StringComparison.Ordinal));

    /// <summary>Whether a type that is in no namespace was written as code, and not made by the compiler.</summary>
    private static bool IsOfTheCode(TypeDefinition type) => string.IsNullOrEmpty(type.Namespace) && !type.Name.StartsWith('<');

    private static string Uncompressed(string address) =>
        address.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? address[..^3] : address;

    private static string Shown(string text)
    {
        var line = Spaces().Replace(text, " ").Trim();

        return line.Length > 160 ? line[..160] + "…" : line;
    }

    // The start of a tag in markup: its name.
    [GeneratedRegex("<([A-Za-z][A-Za-z0-9]*)(?=[\\s/>])")]
    private static partial Regex Element();

    [GeneratedRegex("url\\(|@import", RegexOptions.IgnoreCase)]
    private static partial Regex Load();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
