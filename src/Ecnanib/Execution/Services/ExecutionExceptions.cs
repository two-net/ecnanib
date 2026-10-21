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
internal sealed class ExecutionUnavailableException : Exception
{
    public ExecutionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
