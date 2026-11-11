namespace Ecnanib.Tests.Logging;

/// <summary>
/// Tests that read what the application writes to the console. The console belongs to the whole
/// test process, so these tests do not run alongside any other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>Collects what is written to standard output until it is disposed.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _before = Console.Out;
    private readonly StringWriter _captured = new();

    public ConsoleCapture() => Console.SetOut(TextWriter.Synchronized(_captured));

    public string Text
    {
        get
        {
            lock (_captured)
            {
                return _captured.ToString();
            }
        }
    }

    public void Dispose() => Console.SetOut(_before);
}
