namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// The kind of a failure, fit for the status page every signed-in user can open (NFR-SEC-03, NFR-OBS-01): the exception
/// type, or an exchange error's kind and code. Never the message, which may name a key, an agent, a symbol or a host.
/// The log line written beside each use keeps the detail for the operator.
/// </summary>
public static class ErrorSummary
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var cause = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : exception;
        return cause switch
        {
            ExchangeException { ExchangeCode: { } code } ex => $"{ex.Kind} ({code}) from the exchange",
            ExchangeException ex => $"{ex.Kind} from the exchange",
            _ => cause.GetType().Name,
        };
    }
}
