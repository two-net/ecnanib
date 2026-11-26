using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Components;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Ecnanib.Tests.Texts;

/// <summary>A text that a component holds itself, where the rule finds it.</summary>
/// <param name="Component">The component, by its name in the Web UI: <c>Pages.Account.SignIn</c>.</param>
/// <param name="Where">Where in the component the text is: what it was handed to, or the method that holds it.</param>
/// <param name="Text">The text.</param>
internal sealed record FoundText(string Component, string Where, string Text)
{
    public override string ToString() => $"{Component}, {Where}: \"{Text}\"";
}

/// <summary>What the components of an assembly hold, as the rule reads them.</summary>
/// <param name="Texts">The texts that components hold themselves, in order.</param>
/// <param name="Attributes">The names of the attributes that a component gives a value which it holds itself.</param>
/// <param name="Parameters">The same of the parameters of components, each as <c>Component.Parameter</c>.</param>
internal sealed record ReadComponents(IReadOnlyList<FoundText> Texts, IReadOnlySet<string> Attributes, IReadOnlySet<string> Parameters);

/// <summary>
/// Where the words of the pages are, in one place: text is kept separate from code, so that
/// other languages can be added (NFR-USA-4). A component, which is a page or a part of one,
/// holds no words of its own: it asks a text class for each, such as <c>SignInPages.Title</c>.
/// This file is the source of truth: <c>docs/texts.md</c> explains the rule, and
/// <see cref="TextRuleTests"/> holds the components to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The compiled components are read, and no source file is.</b> What a component writes to a
/// page it hands to the builder of the page, and a word that it holds itself is a literal in
/// what was compiled of it. So the rule reads every component of the assembly, with the parts
/// that the compiler made of it, and finds each literal that is handed over as text.
/// </para>
/// <para>
/// <b>A text of a text class is a property or a method, and never a constant.</b> The compiler
/// copies a constant into whatever uses it, so a page that wrote a constant of a text class
/// would hold the words as if they were typed into it, and nothing could tell the two apart.
/// A name that code uses, such as the name of a form, of a field, or an id, stays a constant:
/// it is handed over where no text is.
/// </para>
/// <para>
/// <b>What is text.</b> In what a component hands to the builder: a letter in markup outside a
/// tag, in what is written as content, in the value of an attribute that is not listed in
/// <see cref="AttributesThatAreNoText"/>, and in the value of a parameter of a component that
/// is not listed in <see cref="ParametersThatAreNoText"/>. And a member of an enum that is
/// written by its name, which is a word of the code. Anywhere else in a component: a literal
/// that reads as words, which is one with a letter and white space, one capitalised word, or
/// capitals and digits. What an exception or a log is told is no text of a page. Punctuation,
/// digits, and signs such as "%" are no words, so a page may write them between two texts.
/// </para>
/// </remarks>
internal static partial class TextRules
{
    private const string Builder = "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";

    /// <summary>
    /// The attributes whose value is no text, by their names: a value of one names something of
    /// the page or tells the browser how to treat it, and nobody reads it as words. An attribute
    /// that is not listed is text, so that one which is added later is held to the rule until
    /// someone decides, here where it shows, that it is none.
    /// </summary>
    public static readonly IReadOnlySet<string> AttributesThatAreNoText = new HashSet<string>(StringComparer.Ordinal)
    {
        // What an element is, and what names it or another element of the page.
        "class", "id", "for", "name", "form", "role", "scope", "type",
        "aria-describedby", "aria-labelledby",

        // Where something leads, and how it is sent.
        "href", "src", "action", "formaction", "method", "target", "rel",

        // How the browser treats a field, and what a form sends for it: a value is the word of
        // the code that the form is read by, and the text of a button stands between its tags.
        "autocomplete", "autocapitalize", "inputmode", "spellcheck", "autofocus", "checked", "value",

        // What a state is, said to a script, a stylesheet, or a screen reader as a word of theirs.
        "aria-atomic", "aria-current", "aria-hidden", "aria-invalid", "aria-live",
        "data-ask", "data-change", "data-enhance-nav", "data-live",

        // How a drawing and a document are read. What a document says of itself to the browser
        // in a "meta" is a word of the browser's, such as how wide the page is.
        "charset", "content", "fill", "preserveAspectRatio", "xmlns",
    };

    /// <summary>
    /// The parameters of components whose value is no text, each as <c>Component.Parameter</c>,
    /// with what it holds instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ParametersThatAreNoText = new Dictionary<string, string>
    {
        ["AuthorizeView.Policy"] = "The name of the policy that says who is shown what is inside.",
        ["FocusOnNavigate.Selector"] = "A selector, which the browser is given to find what takes the focus.",
        ["ExperienceFilter.Action"] = "The address that the form of the filter is sent to.",
        ["RoleStrengthsTable.HeadingId"] = "The id of the heading that names the table.",
        ["RiskCheckRules.LabelledBy"] = "The id of the heading that names the rules of a check.",
    };

    /// <summary>The components of the application, as the rule reads them.</summary>
    public static ReadComponents Application() =>
        Read(typeof(Program).Assembly, "Ecnanib.WebUi", AttributesThatAreNoText, new HashSet<string>(ParametersThatAreNoText.Keys));

    /// <summary>The components of a namespace of an assembly, or nested in it, as the rule reads them.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="namespace">The namespace of the components.</param>
    /// <param name="noText">The attributes whose value is no text.</param>
    /// <param name="noTextParameters">The parameters of components whose value is no text, each as <c>Component.Parameter</c>.</param>
    public static ReadComponents Read(Assembly assembly, string @namespace, IReadOnlySet<string> noText, IReadOnlySet<string> noTextParameters)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(noText);
        ArgumentNullException.ThrowIfNull(noTextParameters);

        var resolver = new DefaultAssemblyResolver();

        foreach (var folder in new[] { assembly, typeof(object).Assembly, typeof(IComponent).Assembly }
                     .Select(known => Path.GetDirectoryName(known.Location))
                     .Where(folder => !string.IsNullOrEmpty(folder))
                     .Distinct())
        {
            resolver.AddSearchDirectory(folder);
        }

        using var read = AssemblyDefinition.ReadAssembly(assembly.Location, new ReaderParameters { AssemblyResolver = resolver });

        var texts = new List<FoundText>();
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new HashSet<string>(StringComparer.Ordinal);

        // Which types are components is asked of the types themselves, and what they hold is
        // read from what was compiled of them.
        var components = assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsNested && IsWithin(type.Namespace, @namespace))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var component in components)
        {
            if (read.MainModule.GetType(component.FullName) is not { } definition)
            {
                continue;
            }

            var name = component.FullName![(@namespace.Length + 1)..];

            foreach (var method in WithNested(definition).SelectMany(type => type.Methods).Where(method => method.HasBody))
            {
                // What the compiler writes for a record says the names of its members, which are
                // words of the code and are written to no page.
                if (!method.Name.StartsWith('<') && method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
                {
                    continue;
                }

                new Reading(name, noText, noTextParameters, texts, attributes, parameters).Read(method);
            }
        }

        return new ReadComponents(texts, attributes, parameters);
    }

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type) =>
        type.NestedTypes.SelectMany(WithNested).Prepend(type);

    /// <summary>The reading of one component: the lists that it is held to, and what is found of it.</summary>
    private sealed class Reading(
        string component,
        IReadOnlySet<string> noText,
        IReadOnlySet<string> noTextParameters,
        List<FoundText> texts,
        HashSet<string> attributes,
        HashSet<string> parameters)
    {
        public void Read(MethodDefinition method)
        {
            // Each short form of an instruction as its long one, so that what loads an argument
            // names the argument.
            method.Body.SimplifyMacros();

            var instructions = method.Body.Instructions;
            var where = method.DeclaringType.IsNested ? $"{method.DeclaringType.Name}.{method.Name}" : method.Name;

            // The components that are open, the last one first: a parameter is of the one that was
            // opened last and is not closed yet.
            var open = new Stack<string>();

            // What was loaded since the builder was, until it is called: the literals, and the
            // members of enums that are handed over as they are.
            List<string>? literals = null;
            List<string>? enums = null;

            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];

                if (literals is null)
                {
                    if (LoadsBuilder(instruction, method))
                    {
                        literals = [];
                        enums = [];
                    }
                    else if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string loose && ReadsAsWords(loose)
                        && !IsToldToCode(instructions, index, method))
                    {
                        texts.Add(new FoundText(component, where, Shown(loose)));
                    }

                    continue;
                }

                if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string literal)
                {
                    literals.Add(literal);

                    continue;
                }

                if (instruction.OpCode == OpCodes.Box && instruction.Operand is TypeReference boxed && IsEnum(boxed))
                {
                    enums!.Add(boxed.Name);

                    continue;
                }

                if (CallOnBuilder(instruction) is not { } call)
                {
                    continue;
                }

                var said = literals.ToArray();

                switch (call.Name)
                {
                    case "OpenComponent":
                        open.Push(call is GenericInstanceMethod { GenericArguments: [var opened] } ? NameOf(opened) : "?");
                        break;

                    case "CloseComponent" when open.Count > 0:
                        open.Pop();
                        break;

                    case "AddMarkupContent":
                        foreach (var markup in said)
                        {
                            ReadMarkup(where, markup);
                        }

                        break;

                    case "AddContent":
                        texts.AddRange(said.Where(HasLetter).Select(word => new FoundText(component, $"{where}, as content", Shown(word))));
                        texts.AddRange(enums!.Select(named => new FoundText(component, $"{where}, as content", $"a member of {named}, by its name")));
                        break;

                    case "AddAttribute" when said is [var attribute, .. var values]:
                        attributes.Add(attribute);

                        if (!noText.Contains(attribute))
                        {
                            texts.AddRange(values.Where(HasLetter).Select(word => new FoundText(component, $"{where}, {attribute}", Shown(word))));
                            texts.AddRange(enums!.Select(named => new FoundText(component, $"{where}, {attribute}", $"a member of {named}, by its name")));
                        }

                        break;

                    case "AddComponentParameter" when said is [var parameter, .. var handed]:
                        var key = $"{(open.Count > 0 ? open.Peek() : "?")}.{parameter}";

                        parameters.Add(key);

                        if (!noTextParameters.Contains(key))
                        {
                            texts.AddRange(handed.Where(HasLetter).Select(word => new FoundText(component, $"{where}, {key}", Shown(word))));
                        }

                        break;
                }

                literals = null;
                enums = null;
            }
        }

        /// <summary>
        /// The text of a piece of markup that a component holds: what stands outside the tags, and
        /// the values of the attributes that are text.
        /// </summary>
        private void ReadMarkup(string where, string markup)
        {
            // In a template every element is taken as it is written, also a cell without its table.
            var document = new HtmlParser().ParseDocument($"<template>{markup}</template>");

            if (document.QuerySelector("template") is not IHtmlTemplateElement template)
            {
                return;
            }

            foreach (var node in template.Content.GetDescendants())
            {
                switch (node)
                {
                    case IText words when words.ParentElement?.LocalName is not ("script" or "style") && HasLetter(words.Data):
                        texts.Add(new FoundText(component, $"{where}, in markup", Shown(words.Data)));
                        break;

                    case IElement element:
                        foreach (var attribute in element.Attributes)
                        {
                            attributes.Add(attribute.Name);

                            if (!noText.Contains(attribute.Name) && HasLetter(attribute.Value))
                            {
                                texts.Add(new FoundText(component, $"{where}, {attribute.Name}", Shown(attribute.Value)));
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>True when an instruction loads the builder of a page: an argument, a local, or a field of that type.</summary>
    private static bool LoadsBuilder(Instruction instruction, MethodDefinition method) => instruction.Operand switch
    {
        ParameterDefinition parameter when instruction.OpCode == OpCodes.Ldarg => parameter.ParameterType.FullName == Builder,
        VariableDefinition variable when instruction.OpCode == OpCodes.Ldloc => variable.VariableType.FullName == Builder,
        FieldReference field when instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldsfld => field.FieldType.FullName == Builder,
        _ => false,
    };

    /// <summary>The method that an instruction calls, when it is one of the builder or one that is handed the builder.</summary>
    private static MethodReference? CallOnBuilder(Instruction instruction) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference called
        && (called.DeclaringType.FullName == Builder || called.Parameters.Any(parameter => parameter.ParameterType.FullName == Builder))
            ? called
            : null;

    /// <summary>
    /// True when a literal is told to the code and to no page: the message of an exception, or
    /// of a log. What takes the literal is found by following the stack from where it is loaded,
    /// through what joins it with other text.
    /// </summary>
    private static bool IsToldToCode(IList<Instruction> instructions, int index, MethodDefinition method)
    {
        for (var steps = 0; steps < 8; steps++)
        {
            if (ConsumerOf(instructions, index, method) is not { } at)
            {
                return false;
            }

            var consumer = instructions[at];

            if (consumer.Operand is not MethodReference taken)
            {
                return false;
            }

            var type = taken.DeclaringType;

            if (type.Name.EndsWith("Exception", StringComparison.Ordinal) || type.Namespace.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal))
            {
                return true;
            }

            // What joins texts hands the whole on: the literal is told to whatever takes that.
            if (type.FullName == "System.String" && taken.Name is "Concat" or "Format" or "Join")
            {
                index = at;

                continue;
            }

            if (type.Name == "DefaultInterpolatedStringHandler")
            {
                // The parts of an interpolated text are gathered, and the whole is taken where it is read out.
                var read = at;

                while (read < instructions.Count
                    && !(instructions[read].Operand is MethodReference { Name: "ToStringAndClear" or "ToString" } whole
                        && whole.DeclaringType.Name == "DefaultInterpolatedStringHandler"))
                {
                    read++;
                }

                if (read >= instructions.Count)
                {
                    return false;
                }

                index = read;

                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Which instruction takes what the instruction at an index leaves on the stack, or null
    /// when the method ends or leaves before one does.
    /// </summary>
    private static int? ConsumerOf(IList<Instruction> instructions, int index, MethodDefinition method)
    {
        // How many values lie on the stack from the one that is followed upwards.
        var depth = 1;
        var at = index + 1;

        for (var steps = 0; steps < 4096 && at < instructions.Count; steps++)
        {
            var instruction = instructions[at];
            var pops = Pops(instruction, method);

            if (pops >= depth)
            {
                return at;
            }

            depth += Pushes(instruction) - pops;

            if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
            {
                return null;
            }

            if (instruction.OpCode.FlowControl == FlowControl.Branch)
            {
                if (instruction.Operand is not Instruction target || instruction.OpCode == OpCodes.Leave)
                {
                    return null;
                }

                at = instructions.IndexOf(target);

                continue;
            }

            at++;
        }

        return null;
    }

    private static int Pops(Instruction instruction, MethodDefinition method)
    {
        if (instruction.OpCode == OpCodes.Ret)
        {
            return method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        }

        return instruction.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            StackBehaviour.Varpop when instruction.Operand is MethodReference called =>
                called.Parameters.Count + (called.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
            _ => int.MaxValue,
        };
    }

    private static int Pushes(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush when instruction.Operand is MethodReference called =>
            instruction.OpCode == OpCodes.Newobj || called.ReturnType.MetadataType != MetadataType.Void ? 1 : 0,
        StackBehaviour.Varpush => 0,
        _ => 1,
    };

    private static bool IsEnum(TypeReference type)
    {
        try
        {
            return type.Resolve() is { IsEnum: true };
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    private static string NameOf(TypeReference type) => type.Name.Split('`')[0];

    /// <summary>True when something that is handed over as text holds a letter: a word, and not only signs and digits.</summary>
    public static bool HasLetter(string said) => said.EnumerateRunes().Any(Rune.IsLetter);

    /// <summary>
    /// True when a literal that is handed over nowhere reads as words: it has a letter and white
    /// space, or is one capitalised word, or is capitals and digits. A name of the code, such as
    /// the name of a form or an id, is none of these.
    /// </summary>
    public static bool ReadsAsWords(string literal) =>
        HasLetter(literal) && (literal.Any(char.IsWhiteSpace) || Capitalised().IsMatch(literal) || Capitals().IsMatch(literal));

    private static string Shown(string text) => Spaces().Replace(text, " ").Trim();

    private static bool IsWithin(string? name, string @namespace) =>
        name is not null && (name == @namespace || name.StartsWith(@namespace + ".", StringComparison.Ordinal));

    [GeneratedRegex(@"^\p{Lu}\p{Ll}+$")]
    private static partial Regex Capitalised();

    [GeneratedRegex(@"^[\p{Lu}\p{Nd}]{2,}$")]
    private static partial Regex Capitals();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
