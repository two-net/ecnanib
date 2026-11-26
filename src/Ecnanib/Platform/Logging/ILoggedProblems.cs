namespace Ecnanib.Platform.Logging;

/// <summary>
/// How many errors and warnings the application logged lately, by the area that logged them. It
/// is the rates of errors that an operator status screen shows (FR-OPS-2). See docs/logging.md.
/// </summary>
/// <remarks>
/// <para>
/// The counts are kept in memory, by this instance, of the events that it logged itself since it
/// started. Nothing of them is written to the database (NFR-DAT-7), and a restart begins them
/// again. Why something went wrong is in the log, and never here: a count holds no word of an
/// event.
/// </para>
/// <para>
/// An event is counted when it is logged, so one that the levels of the configuration file keep
/// out of the log is not counted either.
/// </para>
/// </remarks>
public interface ILoggedProblems
{
    /// <summary>
    /// The counts now: of the last hour and of the last 24 hours, each to the minute, for every
    /// area that logged an error or a warning in that time.
    /// </summary>
    LoggedProblemCounts Read();
}

/// <summary>The errors and warnings that this instance logged lately.</summary>
/// <param name="CountedSince">
/// Since when this instance counts: when it started. Where that is less than a day ago, or less
/// than an hour, a count is of the time since then.
/// </param>
/// <param name="Areas">
/// The areas that logged an error or a warning in the last 24 hours, by name, and after them
/// what was logged outside the application. An area that logged neither is not among them.
/// </param>
public sealed record LoggedProblemCounts(DateTimeOffset CountedSince, IReadOnlyList<AreaProblems> Areas);

/// <summary>The errors and warnings that one area logged lately.</summary>
/// <param name="Area">
/// The area of the application, by the name of its namespace, such as <c>ExchangeGateway</c>, or
/// null for everything that was logged outside the application's own namespaces: the framework,
/// the libraries, and the start of the application.
/// </param>
/// <param name="ErrorsLastHour">The events at the error level or above in the last 60 minutes.</param>
/// <param name="WarningsLastHour">The events at the warning level in the last 60 minutes.</param>
/// <param name="ErrorsLastDay">The events at the error level or above in the last 24 hours.</param>
/// <param name="WarningsLastDay">The events at the warning level in the last 24 hours.</param>
public sealed record AreaProblems(string? Area, int ErrorsLastHour, int WarningsLastHour, int ErrorsLastDay, int WarningsLastDay);
