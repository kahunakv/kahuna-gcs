using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// One real Kahuna server process, started from a published <c>Kahuna.Server.dll</c>.
/// <para>
/// The server tests use the shipped binary on purpose. Only the real host shows whether the published
/// bundle loads through <c>--extension-assembly</c>, and whether its Google dependencies resolve next to
/// the versions of <c>Google.Protobuf</c> and <c>Grpc.*</c> that the server carries. An in-process host
/// would share this test's own dependency graph and could not show either.
/// </para>
/// </summary>
internal sealed class KahunaServerProcess : IAsyncDisposable
{
    internal const string ServerVariable = "KAHUNA_SERVER_DLL";

    internal const string BundleVariable = "KAHUNA_GCS_BUNDLE_DLL";

    private readonly Process process;

    private readonly StringBuilder output = new();

    private readonly Lock outputSync = new();

    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KahunaServerProcess(string name, Process process, int httpPort, int grpcPort)
    {
        Name = name;
        this.process = process;
        HttpPort = httpPort;
        GrpcPort = grpcPort;
        BaseAddress = new Uri($"http://localhost:{httpPort}/");
    }

    internal string Name { get; }

    internal int HttpPort { get; }

    internal int GrpcPort { get; }

    internal Uri BaseAddress { get; }

    internal bool HasExited => exited.Task.IsCompleted;

    internal int ExitCode => process.ExitCode;

    /// <summary>Everything the process wrote to stdout and stderr so far.</summary>
    internal string Output
    {
        get
        {
            lock (outputSync)
                return output.ToString();
        }
    }

    /// <summary>The published server, from <see cref="ServerVariable"/>, or null when it is not set.</summary>
    internal static string? ServerDll => ExistingFile(ServerVariable);

    /// <summary>The published extension bundle DLL, from <see cref="BundleVariable"/>, or null when it is not set.</summary>
    internal static string? BundleDll => ExistingFile(BundleVariable);

    /// <summary>Why the server tests cannot run on this machine, or null when they can.</summary>
    internal static string? SkipReason =>
        ServerDll is not null && BundleDll is not null
            ? null
            : $"No published Kahuna server or extension bundle. Set {ServerVariable} to a published Kahuna.Server.dll and " +
              $"{BundleVariable} to the Kahuna.Storage.Gcs.dll from scripts/publish-bundle.sh.";

    internal static KahunaServerProcess Start(string name, string workingDirectory, int httpPort, int grpcPort, IEnumerable<string> arguments)
    {
        ProcessStartInfo info = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        info.ArgumentList.Add(ServerDll ?? throw new InvalidOperationException(SkipReason));
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        // The console logger buffers less and keeps one line per entry without colour codes.
        info.Environment["Logging__Console__FormatterName"] = "simple";
        info.Environment["Logging__Console__FormatterOptions__ColorBehavior"] = "Disabled";

        Process process = new() { StartInfo = info, EnableRaisingEvents = true };
        KahunaServerProcess server = new(name, process, httpPort, grpcPort);

        process.OutputDataReceived += (_, e) => server.Append(e.Data);
        process.ErrorDataReceived += (_, e) => server.Append(e.Data);
        process.Exited += (_, _) => server.exited.TrySetResult();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return server;
    }

    /// <summary>
    /// Waits until the node reports that it can serve. Fails at once when the process exits, with its
    /// output, so a start failure is reported as itself and not as a timeout.
    /// </summary>
    internal async Task WaitUntilReadyAsync(HttpClient http, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        Uri health = new(BaseAddress, "v1/cluster/health");

        while (true)
        {
            if (HasExited)
                throw new InvalidOperationException($"Node {Name} exited with code {ExitCode} before it was ready.\n{Output}");

            try
            {
                using HttpResponseMessage response = await http.GetAsync(health, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
                // The listener is not bound yet.
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Node {Name} was not ready after {timeout}.\n{Output}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Node {Name} was not ready after {timeout}.\n{Output}");
            }
        }
    }

    /// <summary>Waits for the process to exit. Returns false when it still runs after the timeout.</summary>
    internal async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task finished = await Task.WhenAny(exited.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return finished == exited.Task;
    }

    /// <summary>A free TCP port on the loopback interface.</summary>
    internal static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        // A test result does not depend on a graceful shutdown, and a clustered node can take most of
        // a minute to drain one. This kills only the process this instance started.
        if (!HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the check and the kill.
            }

            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        }

        process.Dispose();
    }

    public override string ToString() => Name;

    private void Append(string? line)
    {
        if (line is null)
            return;

        lock (outputSync)
            output.AppendLine(line);
    }

    private static string? ExistingFile(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
