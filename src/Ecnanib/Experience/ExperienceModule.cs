namespace Ecnanib.Experience;

/// <summary>
/// Experience: track record, strength scores, lessons (SRS 3.1). See docs/experience.md for what
/// of it is built.
/// </summary>
public static class ExperienceModule
{
    /// <summary>
    /// Registers the component: what works out the track record of each provider and of each
    /// model from the closed trades of a user (FR-EXP-2), with Demo and Live apart (FR-EXP-9).
    /// </summary>
    public static IServiceCollection AddExperience(this IServiceCollection services)
    {
        // The trades are asked of whatever keeps closed positions, through IClosedTrades, which
        // the Decision Engine implements: Experience may not use it. Nothing is stored here.
        services.AddSingleton<ITrackRecords, TrackRecords>();

        return services;
    }
}
