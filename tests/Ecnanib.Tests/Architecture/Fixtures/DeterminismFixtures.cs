// Deliberately broken code for RiskEngineDeterminismGuardTests. None of it ever runs. Each class hides one
// nondeterministic use in a different way, except PureDecimalMath and Wiring, which have to pass.

namespace Ecnanib.Tests.Architecture.DeterminismFixtures;

public static class ReadsTheClock
{
    public static DateTime Now() => DateTime.UtcNow;
}

public static class ReadsTheClockInALambda
{
    public static Func<DateTimeOffset> Now() => () => DateTimeOffset.UtcNow;
}

public static class TakesATimeProvider
{
    public static DateTimeOffset Now(TimeProvider time) => time.GetUtcNow();
}

public static class IsRandom
{
    public static int Roll() => Random.Shared.Next(6);
}

public static class MakesAGuid
{
    public static Guid Id() => Guid.NewGuid();
}

public static class RunsAsynchronously
{
    public static async Task<decimal> SizeAsync(decimal quantity)
    {
        await Task.Yield();

        return quantity;
    }
}

public static class ReadsAFile
{
    public static string Read() => File.ReadAllText("caps.json");
}

public static class ReadsTheMachine
{
    public static int Processors() => Environment.ProcessorCount;
}

public sealed class StoresADouble
{
    public double Ratio { get; set; }
}

public static class ConvertsToDouble
{
    public static decimal Round(decimal value) => (decimal)Math.Round((double)value, 2);
}

public static class ReturnsAFloat
{
    public static float Half() => 0.5f;
}

public static class PureDecimalMath
{
    public static decimal Down(decimal value, decimal step, decimal origin) =>
        step <= 0m ? value : origin + (decimal.Floor((value - origin) / step) * step);

    public static decimal Smallest(IEnumerable<decimal> values) => values.Where(value => value > 0m).DefaultIfEmpty().Min();
}

/// <summary>Stands in for the module's entry class, which may wire things up with the clock and tasks.</summary>
public static class Wiring
{
    public static Task<DateTimeOffset> StartAsync() => Task.FromResult(DateTimeOffset.UtcNow);
}
