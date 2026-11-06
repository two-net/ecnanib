using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>How the contracts write names: snake case, with FR-CYC-03's actions in capitals, as the SRS writes them.</summary>
internal static class ContractNames
{
    public static string Action(TradeAction action) => JsonNamingPolicy.SnakeCaseUpper.ConvertName(action.ToString());

    public static string Of<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

/// <summary>
/// Reads an answer in three steps: parse it, check its structure against the schema, then read it into the role's
/// record and apply <see cref="ContractRules"/>. Every violation is collected, so one retry can fix them all
/// (FR-CYC-05). See Output contracts in docs/architecture.md.
/// </summary>
internal static class ContractReader
{
    private const string Fence = "```";

    /// <summary>
    /// Strict, so a record that drifts from its schema fails the tests: every constructor parameter is required, a
    /// non-nullable one can't be null, and an unknown property is refused.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters =
        {
            new JsonStringEnumConverter<TradeAction>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
    };

    /// <summary>
    /// The output in its contract's names, as <see cref="Read"/> takes it, so writing then reading gives the same
    /// output. Without the role, which isn't in any contract.
    /// </summary>
    public static string Write(RoleOutput output)
    {
        var node = JsonSerializer.SerializeToNode(output, output.GetType(), Options)!.AsObject();
        node.Remove(JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(RoleOutput.Role)));

        return node.ToJsonString();
    }

    public static ContractCheck Read(AiRole role, string answer, ContractContext context, bool truncated)
    {
        var violations = new Violations();

        if (truncated)
        {
            // An answer cut short is almost never valid JSON, and saying why is more use than where it broke.
            violations.Add("$", "was cut off at the output limit, so it's incomplete: answer more briefly");
            return Failed(violations);
        }

        var text = Unfenced(answer);

        if (string.IsNullOrWhiteSpace(text))
        {
            violations.Add("$", "is empty");
            return Failed(violations);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            // The exception's message can quote the answer, so only its position is used.
            violations.Add(
                "$",
                $"isn't one JSON object and nothing else: it stops being valid JSON at line {exception.LineNumber + 1}, "
                + $"byte {exception.BytePositionInLine + 1}");
            return Failed(violations);
        }

        using (document)
        {
            ContractSchemas.NodeFor(role, context.Market).Validate(document.RootElement, "$", violations);

            if (violations.Any)
            {
                return Failed(violations);
            }

            RoleOutput output;
            try
            {
                output = (RoleOutput)document.RootElement.Deserialize(TypeOf(role), Options)!;
            }
            catch (JsonException exception)
            {
                // The structure is the schema's by now, so every name in the path is the contract's own.
                violations.Add(exception.Path ?? "$", "can't be read");
                return Failed(violations);
            }

            var checkedOutput = ContractRules.Check(output, context, violations);

            return violations.Any ? Failed(violations) : new ContractCheck(checkedOutput, []);
        }
    }

    /// <summary>
    /// Takes the answer out of one Markdown code fence, which a model may add when its provider doesn't enforce the
    /// schema. Only a fence around the whole answer, so text before or after it still fails.
    /// </summary>
    private static string Unfenced(string answer)
    {
        var text = answer.Trim();

        if (!text.StartsWith(Fence, StringComparison.Ordinal) || !text.EndsWith(Fence, StringComparison.Ordinal) || text.Length < 2 * Fence.Length)
        {
            return text;
        }

        var firstLine = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstLine < 0)
        {
            return text;
        }

        var language = text[Fence.Length..firstLine].Trim();
        if (language.Length > 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return text[(firstLine + 1)..^Fence.Length].Trim();
    }

    private static Type TypeOf(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => typeof(TechnicalAssessment),
        AiRole.SentimentAnalyst => typeof(SentimentAssessment),
        AiRole.Strategist => typeof(TradeProposal),
        AiRole.RiskCritic => typeof(CriticVerdict),
        AiRole.Reviewer => typeof(TradeReflection),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a role."),
    };

    private static ContractCheck Failed(Violations violations) => new(null, violations.Reported());
}
