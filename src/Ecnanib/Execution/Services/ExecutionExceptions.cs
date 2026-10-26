namespace Ecnanib.Execution.Services;

/// <summary>
/// The agent cannot trade as configured (hedge mode on the account, a leverage the exchange refuses, FR-EXE-11): the
/// pipeline moves it to Error with the message once the evaluation is recorded.
/// </summary>
internal sealed class ExecutionFaultException : Exception
{
    public ExecutionFaultException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The venue cannot be reached for this agent right now (its key no longer resolves, the symbol left the listing); the action fails and the state stands.</summary>
internal class ExecutionUnavailableException : Exception
{
    public ExecutionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The answer to an order request was lost and the exchange could not be asked whether it has the order (NFR-REL-02):
/// the row stays New for reconciliation, nothing is retried with a new id and nothing is flattened, because a second
/// order against an exchange that may hold the first is the one thing worse than waiting.
/// </summary>
internal sealed class OrderOutcomeUnknownException : ExecutionUnavailableException
{
    public OrderOutcomeUnknownException(Database.Entities.Order row, string message, Exception innerException)
        : base(message, innerException)
    {
        Row = row;
    }

    public Database.Entities.Order Row { get; }
}
