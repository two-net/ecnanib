using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Ecnanib.Tests.Support;

/// <summary>
/// The built application started as a child process, the way an operator runs it: <c>dotnet exec Ecnanib.dll …</c> on
/// the runtime the tests run on, from the working directory the test chooses, with both output streams captured line
/// by line. The application reads no environment variable (FR-CFG-02), so the test's environment is passed through
/// untouched. Dispose kills whatever is still running.
/// </summary>
internal sealed class ApplicationProcess : IAsyncDisposable
{
    public static readonly string ApplicationDll = Path.Combine(AppContext.BaseDirectory, "Ecnanib.dll");

    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    public const string SkipMessage = "Process test: Ecnanib.dll is not beside the test assembly (build the solution) or no dotnet host was found.";

    private const int SigTerm = 15;

    private readonly Process _process;
    private readonly List<(bool Error, string Line)> _lines = [];
    private readonly TaskCompletionSource _outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _drained;

    private ApplicationProcess(Process process)
    {
        _process = process;
    }

    public static bool IsAvailable => File.Exists(ApplicationDll) && DotnetHost() is not null;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    /// <summary>Both streams in arrival order.</summary>
    public IReadOnlyList<string> Output => Lines(null);

    public IReadOnlyList<string> StandardOutput => Lines(error: false);

    public IReadOnlyList<string> StandardError => Lines(error: true);

    public string Transcript => string.Join(Environment.NewLine, Output);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ApplicationProcess Start(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(DotnetHost() ?? throw new InvalidOperationException(SkipMessage))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(ApplicationDll);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var application = new ApplicationProcess(process);
        process.OutputDataReceived += (_, e) => application.OnLine(error: false, e.Data);
        process.ErrorDataReceived += (_, e) => application.OnLine(error: true, e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return application;
    }

    /// <summary>The first line of either stream that matches; throws with the transcript when the process exits first or the timeout passes.</summary>
    public async Task<string> WaitForLineAsync(Regex pattern, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var drained = Drained;
        var seen = 0;
        while (true)
        {
            Ct.ThrowIfCancellationRequested();
            Task changed;
            lock (_lines)
            {
                for (; seen < _lines.Count; seen++)
                {
                    if (pattern.IsMatch(_lines[seen].Line))
                        return _lines[seen].Line;
                }

                changed = _changed.Task;
            }

            if (drained.IsCompleted)
                throw new InvalidOperationException($"The application exited with code {_process.ExitCode} before a line matched {pattern}:{Environment.NewLine}{Transcript}");
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"No line matched {pattern} within {timeout}:{Environment.NewLine}{Transcript}");

            // The delay is cancelled as soon as a line or the exit wins, so no timer outlives its turn.
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var delay = Task.Delay(remaining, timer.Token);
            var winner = await Task.WhenAny(changed, drained, delay);
            await timer.CancelAsync();
            if (winner == delay)
                await delay; // throws when the test itself was cancelled; otherwise the deadline passed and the loop reports it
        }
    }

    /// <summary>The exit code once the process has ended and both streams are drained.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            await Drained.WaitAsync(timeout, Ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The application did not exit within {timeout}:{Environment.NewLine}{Transcript}");
        }

        return _process.ExitCode;
    }

    /// <summary>
    /// SIGTERM, as a service manager stops the process; the host's console lifetime turns it into a clean stop and the
    /// exit code is returned. Windows has no SIGTERM: the process is killed and null returned.
    /// </summary>
    public async Task<int?> StopAsync(TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            _process.Kill();
            await WaitForExitAsync(timeout);
            return null;
        }

        if (!_process.HasExited && Kill(_process.Id, SigTerm) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return await WaitForExitAsync(timeout);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(grace.Token);
            }
        }
        catch (Exception)
        {
            // A process already gone, or a descendant beyond reach: cleanup must not replace the test's own failure.
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>Exit and the end of both streams; one task for the process's lifetime.</summary>
    private Task Drained => _drained ??= Task.WhenAll(_process.WaitForExitAsync(), _outputClosed.Task, _errorClosed.Task);

    private IReadOnlyList<string> Lines(bool? error)
    {
        lock (_lines)
            return _lines.Where(l => error is null || l.Error == error).Select(l => l.Line).ToList();
    }

    private void OnLine(bool error, string? data)
    {
        if (data is null)
        {
            (error ? _errorClosed : _outputClosed).TrySetResult();
            return;
        }

        lock (_lines)
        {
            _lines.Add((error, data));
            var changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    /// <summary>
    /// The <c>dotnet</c> muxer: the one the SDK names, else the one that owns the runtime these tests run on
    /// (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c> is three levels below it), else the test host itself when it is the muxer.
    /// </summary>
    private static string? DotnetHost()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtime is not null)
        {
            var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", name));
            if (File.Exists(root))
                return root;
        }

        if (Environment.ProcessPath is { } own && string.Equals(Path.GetFileName(own), name, StringComparison.OrdinalIgnoreCase))
            return own;
        return null;
    }

#pragma warning disable SYSLIB1054 // a plain kill(2); nothing to marshal
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
#pragma warning restore SYSLIB1054
}
