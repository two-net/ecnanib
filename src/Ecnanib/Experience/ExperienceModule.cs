using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Experience;

/// <summary>
/// Experience: track record, strength scores, lessons (SRS 3.1). See docs/experience.md for what
/// of it is built.
/// </summary>
public static class ExperienceModule
{
    /// <summary>
    /// Registers the component: what works out the track record of each provider and of each
    /// model from the closed trades of a user (FR-EXP-2), with Demo and Live apart (FR-EXP-9),
    /// what works out the measure and the strength score of each model in each role (FR-EXP-3,
    /// FR-EXP-4), and the job that scores the calls of the analysts and follows the shadow
    /// trades of the vetoed plans (FR-EXP-5).
    /// </summary>
    public static IServiceCollection AddExperience(this IServiceCollection services)
    {
        // The trades are asked of whatever keeps closed positions, through IClosedTrades, which
        // the Decision Engine implements: Experience may not use it. Nothing of a track record
        // is stored here.
        services.AddSingleton<ITrackRecords, TrackRecords>();

        // What is stored here is what later candles are to say something of: the calls of the
        // analysts and the shadow trades, which the Decision Engine writes with each cycle. The
        // job gives each its outcome, and the strength scores are worked out from them.
        services.AddSingleton<IRoleStrengths, RoleStrengths>();
        services.AddSingleton<ExperienceScoring>();
        services.AddBackgroundJob<ExperienceScoringJob>();

        return services;
    }
}
