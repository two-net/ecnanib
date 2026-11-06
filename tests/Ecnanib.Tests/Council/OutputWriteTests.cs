using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Tests.Council;

/// <summary>
/// A validated output is written in its contract's names, which is how the trace stores it and the next roles read it,
/// so reading what was written gives the same output.
/// </summary>
public sealed class OutputWriteTests
{
    [Theory]
    [InlineData(AiRole.TechnicalAnalyst)]
    [InlineData(AiRole.SentimentAnalyst)]
    [InlineData(AiRole.Strategist)]
    [InlineData(AiRole.RiskCritic)]
    [InlineData(AiRole.Reviewer)]
    public void Writing_an_output_then_checking_it_gives_it_back(AiRole role)
    {
        var context = ContractSamples.ContextFor(role);
        var output = OutputContracts.Check(role, ContractSamples.For(role), context).Output!;

        var written = OutputContracts.Write(output);
        var read = OutputContracts.Check(role, written, context);

        Assert.Empty(read.Violations);
        Assert.Equal(written, OutputContracts.Write(read.Output!));
    }

    [Fact]
    public void An_action_is_written_as_the_srs_names_it()
    {
        var written = OutputContracts.Write(ContractSamples.ProposalOutput());

        Assert.Contains("\"action\":\"OPEN_LONG\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"role\"", written, StringComparison.Ordinal);
    }
}
