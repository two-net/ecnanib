using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Ecnanib.Platform.Settings;
using Npgsql;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Runs a client tool of PostgreSQL, <c>pg_dump</c> or <c>pg_restore</c>, against the server
/// that the configuration file names (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the one type of the application that starts a process,</b> and it starts the two
/// tools that the <c>Backup</c> section names, and nothing else.
/// </para>
/// <para>
/// <b>Nothing secret is on a command line,</b> which every account of the machine can read. The
/// tool is told where the server is and who asks in the variables of its own process, which
/// the client library of PostgreSQL reads. The database is an argument, and is no secret.
/// </para>
/// <para>
/// <b>What a tool reads and writes goes through the application:</b> a backup is what the tool
/// wrote to its standard output, and a restore reads it from its standard input. So the tool
/// is given no path, and need not run where the files are.
/// </para>
/// </remarks>
internal sealed class PostgresTools(ConnectionStringsSettings connection)
{
    // What is kept of what a tool wrote to its standard error, for the error that says why it failed.
    private const int MostErrorText = 4000;

    /// <summary>Runs a tool until it ends.</summary>
    /// <param name="tool">The tool, as the configuration file names it.</param>
    /// <param name="arguments">Its arguments, none of which is a secret.</param>
    /// <param name="input">What the tool reads from its standard input, or null when it reads nothing.</param>
    /// <param name="output">Where what the tool writes to its standard output goes, or null when that is dropped.</param>
    /// <param name="cancellationToken">Stops the tool, and whatever it started.</param>
    /// <exception cref="PostgresToolException">The tool could not be started, or it ended with an error.</exception>
    public async Task RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        Stream? input,
        Stream? output,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartOf(tool, arguments, readsInput: input is not null) };

        try
        {
            process.Start();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            throw new PostgresToolException(
                $"{tool} could not be started. The PostgreSQL client tools must be installed where the application runs: {error.Message}",
                error);
        }

        // Each of the three pipes is read or written for as long as the tool runs: a pipe that
        // nobody reads fills, and the tool then waits for good.
        var errorText = ReadErrorAsync(process, cancellationToken);
        var written = process.StandardOutput.BaseStream.CopyToAsync(output ?? Stream.Null, cancellationToken);
        var read = input is null ? Task.CompletedTask : WriteInputAsync(process, input, cancellationToken);

        try
        {
            await Task.WhenAll(read, written, errorText, process.WaitForExitAsync(cancellationToken));
        }
        catch
        {
            // Stopped, or what the tool wrote could not be kept: the tool has nothing left to do.
            Stop(process);
            await EndedAsync(read, written, errorText);

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new PostgresToolException($"{tool} ended with code {process.ExitCode}: {(await errorText).Trim()}");
        }
    }

    private ProcessStartInfo StartOf(string tool, IReadOnlyList<string> arguments, bool readsInput)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = readsInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The tool goes by what the configuration file says of the connection, and by nothing
        // that the process of the application happens to carry for a PostgreSQL client.
        foreach (var inherited in start.Environment.Keys.Where(name => name.StartsWith("PG", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(inherited);
        }

        var server = new NpgsqlConnectionStringBuilder(connection.EcnanibDbContext.Reveal());

        Set(start, "PGHOST", server.Host);
        Set(start, "PGPORT", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(start, "PGUSER", server.Username);
        Set(start, "PGPASSWORD", server.Password);
        Set(start, "PGPASSFILE", server.Passfile);
        Set(start, "PGSSLMODE", NameOf(server.SslMode));
        Set(start, "PGSSLROOTCERT", server.RootCertificate);

        return start;
    }

    private static void Set(ProcessStartInfo start, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            start.Environment[name] = value;
        }
    }

    /// <summary>What the client library of PostgreSQL calls a mode of TLS that the driver names.</summary>
    private static string NameOf(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => throw new InvalidOperationException($"The connection string names a mode of TLS that the tools are not told: {mode}."),
    };

    private static async Task WriteInputAsync(Process process, Stream input, CancellationToken cancellationToken)
    {
        var pipe = process.StandardInput.BaseStream;
        try
        {
            await input.CopyToAsync(pipe, cancellationToken);
        }
        catch (IOException)
        {
            // The tool has ended, or reads no more. Why is in how it ended, and in what it wrote.
        }
        finally
        {
            // The end of the input is what tells the tool that it has all of it.
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The pipe is closed already.
            }
        }
    }

    private static async Task<string> ReadErrorAsync(Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];

        // Read to the end, also past what is kept: the tool would wait on a pipe that is full.
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer, cancellationToken)) > 0)
        {
            text.Append(buffer, 0, Math.Min(count, MostErrorText - text.Length));
        }

        return text.ToString();
    }

    /// <summary>
    /// Waits until nothing reads or writes a pipe of a tool that was stopped any more. How each
    /// ended does not matter: the error that stopped the tool is the one that is passed on.
    /// </summary>
    private static async Task EndedAsync(params Task[] pipes)
    {
        foreach (var pipe in pipes)
        {
            try
            {
                await pipe;
            }
            catch (Exception)
            {
                // The pipe closed with the tool, or it is what failed, and that error is on its way out.
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It has ended already, or cannot be ended: neither is worth more than the error on its way out.
        }
    }
}

/// <summary>A client tool of PostgreSQL could not be started, or ended with an error.</summary>
internal sealed class PostgresToolException(string message, Exception? cause = null) : Exception(message, cause);
