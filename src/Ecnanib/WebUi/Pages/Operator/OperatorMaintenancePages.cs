using System.Globalization;
using Ecnanib.Platform.Availability;

namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>What a field of the form that announces maintenance was refused for.</summary>
public enum MaintenanceFieldProblem
{
    /// <summary>What was typed is no date with a time.</summary>
    NotATime,

    /// <summary>The start is now or earlier.</summary>
    StartsTooSoon,

    /// <summary>The end is the start or earlier.</summary>
    EndsBeforeItStarts,

    /// <summary>The end is later after the start than maintenance may last.</summary>
    LastsTooLong,
}

/// <summary>
/// The texts of the page on which an operator announces maintenance, cancels it, and ends it,
/// in one place (NFR-USA-4, NFR-REL-6), and how the page reads a time that was typed into its
/// form. The operator status screen leads to the page. See "Announced maintenance" in
/// docs/reliability.md.
/// </summary>
public static class OperatorMaintenancePages
{
    /// <summary>The name of the form that announces maintenance.</summary>
    public const string AnnounceForm = "announce-maintenance";

    /// <summary>The name of the form that cancels maintenance, and ends it.</summary>
    public const string ChangeForm = "change-maintenance";

    /// <summary>The id of the heading of what is announced, which names its table.</summary>
    public const string AnnouncedId = "maintenance-announced";

    /// <summary>The id of the field of the start.</summary>
    public const string StartsId = "maintenance-starts";

    /// <summary>The id of the field of the end.</summary>
    public const string EndsId = "maintenance-ends";

    // How a date and a time of day are typed: as the hints say, and with the letter between
    // them that a moment has where it is copied from a file or a log.
    private static readonly string[] Typed = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm"];

    /// <summary>The title of the page in the browser, its heading, and the text of the link that leads to it.</summary>
    public static string Title => "Maintenance";

    /// <summary>What announcing maintenance does.</summary>
    public static string Explained =>
        "Maintenance that is announced here is told of on every page, to visitors and to users, from now until it is over: " +
        "from when to when the service may not answer.";

    /// <summary>What announcing maintenance does not do (NFR-REL-6).</summary>
    public static string ActsOnNoAgent =>
        "An announcement pauses no agent, takes no order off the exchange, and closes no position. Open positions keep " +
        "their protective orders on the exchange while the service does not answer.";

    /// <summary>When maintenance is left out of the availability of the service (NFR-REL-1).</summary>
    /// <param name="noticeHours">How many hours before its start maintenance is announced in advance.</param>
    public static string LeftOutWhen(int noticeHours) => noticeHours switch
    {
        0 => "What the service is down for during maintenance is left out of its availability.",
        1 =>
            "What the service is down for during maintenance is left out of its availability when the maintenance was " +
            "announced at least 1 hour before it starts. Maintenance that is announced later is told of all the same, and counts.",
        _ =>
            "What the service is down for during maintenance is left out of its availability when the maintenance was " +
            $"announced at least {Hours(noticeHours)} hours before it starts. Maintenance that is announced later is told of all the same, and counts.",
    };

    /// <summary>That maintenance was announced, said at the top of the page after the form.</summary>
    public static string Done => "The maintenance is announced. Every page tells of it from now on.";

    /// <summary>That the maintenance which was just announced counts, because it was announced late.</summary>
    /// <param name="noticeHours">How many hours before its start maintenance is announced in advance.</param>
    public static string DoneLate(int noticeHours) =>
        "The maintenance is announced. Every page tells of it from now on. It was announced later than " +
        $"{Hours(noticeHours)} hours before it starts, so what the service is down for during it counts.";

    /// <summary>The heading of what is announced.</summary>
    public static string AnnouncedHeading => "Announced maintenance";

    /// <summary>That nothing is announced.</summary>
    public static string NoneAnnounced => "No maintenance is announced.";

    /// <summary>The name of the table of what is announced.</summary>
    public static string AnnouncedCaption => "The maintenance that is announced and not over";

    /// <summary>The heading of when maintenance starts.</summary>
    public static string StartsHeading => "Starts";

    /// <summary>The heading of when it ends.</summary>
    public static string EndsHeading => "Ends";

    /// <summary>The heading of whether it has started.</summary>
    public static string StateHeading => "State";

    /// <summary>The heading of whether it is left out of the availability.</summary>
    public static string LeftOutHeading => "Left out of the availability";

    /// <summary>The heading of what an operator can do with it.</summary>
    public static string ChangeHeading => "Change";

    /// <summary>Whether maintenance has started.</summary>
    public static string StateOf(AnnouncedMaintenance maintenance)
    {
        ArgumentNullException.ThrowIfNull(maintenance);

        return maintenance.UnderWay ? "Under way" : "Announced";
    }

    /// <summary>Whether maintenance is left out of the availability, which it is when it was announced in advance.</summary>
    public static string LeftOut(AnnouncedMaintenance maintenance)
    {
        ArgumentNullException.ThrowIfNull(maintenance);

        return maintenance.InAdvance ? "Yes" : "No: it was announced late";
    }

    /// <summary>The id of the cell that says when the maintenance of a row starts, which says what the button of the row is about.</summary>
    /// <param name="row">The number of the row, from 1.</param>
    public static string RowId(int row) => "maintenance-" + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text of the button that cancels maintenance which has not started.</summary>
    public static string Cancel => "Cancel";

    /// <summary>The text of the button that ends maintenance which is under way.</summary>
    public static string EndNow => "End now";

    /// <summary>What the two buttons do, said under the table.</summary>
    public static string ChangeMeans =>
        "Maintenance that has not started can be cancelled: no page tells of it any more. Maintenance that is under way " +
        "can be ended: it is over from then on, and what the service is down for after that counts.";

    /// <summary>The heading of the form.</summary>
    public static string AnnounceHeading => "Announce maintenance";

    /// <summary>The label of the field of the start.</summary>
    public static string StartsLabel => "Starts, in UTC";

    /// <summary>The label of the field of the end.</summary>
    public static string EndsLabel => "Ends, in UTC";

    /// <summary>The hint of the field of the start, which says how a date and a time of day are typed.</summary>
    public static string StartsHint => "A date and a time of day in UTC, as in 2026-11-28 02:00, later than now.";

    /// <summary>The hint of the field of the end.</summary>
    /// <param name="longestHours">How many hours maintenance lasts at the most.</param>
    public static string EndsHint(int longestHours) => longestHours == 1
        ? "A date and a time of day in UTC, as in 2026-11-28 04:00, no more than 1 hour after the start."
        : $"A date and a time of day in UTC, as in 2026-11-28 04:00, no more than {Hours(longestHours)} hours after the start.";

    /// <summary>Why a field was refused.</summary>
    /// <param name="problem">What is wrong with it.</param>
    /// <param name="longestHours">How many hours maintenance lasts at the most.</param>
    public static string Says(MaintenanceFieldProblem problem, int longestHours) => problem switch
    {
        MaintenanceFieldProblem.NotATime => "Enter a date and a time of day, as in 2026-11-28 02:00.",
        MaintenanceFieldProblem.StartsTooSoon => "Maintenance is announced before it starts. Enter a start that is later than now.",
        MaintenanceFieldProblem.EndsBeforeItStarts => "Enter an end that is later than the start.",
        MaintenanceFieldProblem.LastsTooLong when longestHours == 1 => "Maintenance lasts 1 hour at the most. Enter an earlier end.",
        MaintenanceFieldProblem.LastsTooLong => $"Maintenance lasts {Hours(longestHours)} hours at the most. Enter an earlier end.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    /// <summary>The text of the button that announces maintenance.</summary>
    public static string Announce => "Announce maintenance";

    /// <summary>That each action is recorded, said under the form (FR-OPS-5).</summary>
    public static string Recorded =>
        "Each announcement, each cancellation, and each early end is recorded in the audit trail, with who made it and when.";

    /// <summary>
    /// Reads a date and a time of day that was typed into a field of the form, as a moment in
    /// UTC: year, month, and day, then hours and minutes, as in <c>2026-11-28 02:00</c>. False
    /// when it is none.
    /// </summary>
    /// <param name="typed">What the field sent.</param>
    /// <param name="moment">The moment, when it is one.</param>
    public static bool TryRead(string? typed, out DateTimeOffset moment)
    {
        if (DateTime.TryParseExact(typed?.Trim(), Typed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var read))
        {
            moment = new DateTimeOffset(DateTime.SpecifyKind(read, DateTimeKind.Unspecified), TimeSpan.Zero);
            return true;
        }

        moment = default;
        return false;
    }

    /// <summary>Which field what is wrong with an announcement is said at, and as what.</summary>
    /// <param name="problem">What the service found wrong.</param>
    public static MaintenanceFieldProblem Of(MaintenanceProblem problem) => problem switch
    {
        MaintenanceProblem.StartsTooSoon => MaintenanceFieldProblem.StartsTooSoon,
        MaintenanceProblem.EndsBeforeItStarts => MaintenanceFieldProblem.EndsBeforeItStarts,
        MaintenanceProblem.LastsTooLong => MaintenanceFieldProblem.LastsTooLong,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    private static string Hours(int hours) => hours.ToString(CultureInfo.InvariantCulture);
}
