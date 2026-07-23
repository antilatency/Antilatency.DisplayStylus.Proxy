using System.Diagnostics;
using System.Text;
using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class HttpServerFixture : IAsyncLifetime {
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private string? _serverDll;
    private string? _repositoryRoot;

    public Uri BaseAddress { get; } = new(ProxyProtocol.LoopbackBaseUrl + "/");

    public async ValueTask InitializeAsync() {
        EnsurePortIsAvailable();

        _repositoryRoot = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        _serverDll = Path.Combine(
            _repositoryRoot,
            "tests",
            "Antilatency.DisplayStylus.Proxy.TestHost",
            "bin",
            configuration,
            "net10.0",
            "Antilatency.DisplayStylus.Proxy.TestHost.dll");

        if (!File.Exists(_serverDll)) {
            throw new FileNotFoundException(
                "The HTTP integration tests require the server project to be built first.",
                _serverDll);
        }

        await StartServerAsync();
    }

    public async Task RestartAsync(TimeSpan? downtime = null) {
        await StopServerAsync();
        EnsurePortIsAvailable();
        if (downtime is { } delay && delay > TimeSpan.Zero) {
            await Task.Delay(delay);
        }
        await StartServerAsync();
    }

    public void ClearLogs() {
        lock (_output) {
            _output.Clear();
        }
        lock (_errors) {
            _errors.Clear();
        }
    }

    public string GetLogs() => $"{Snapshot(_output)}\n{Snapshot(_errors)}";

    public async Task<ProcessResult> RunSecondInstanceAsync() {
        if (_repositoryRoot is null || _serverDll is null) {
            throw new InvalidOperationException("The HTTP server fixture is not initialized.");
        }

        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_serverDll);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start the second proxy process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    public async ValueTask DisposeAsync() {
        await StopServerAsync();
    }

    private async Task StartServerAsync() {
        if (_process is not null || _repositoryRoot is null || _serverDll is null) {
            throw new InvalidOperationException("The HTTP server fixture is not ready to start a process.");
        }

        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_serverDll);
        startInfo.ArgumentList.Add("--Logging:LogLevel:Default=Warning");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => Append(_output, args.Data);
        _process.ErrorDataReceived += (_, args) => Append(_errors, args.Data);
        if (!_process.Start()) {
            throw new InvalidOperationException("Failed to start the proxy server process.");
        }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var client = new HttpClient { BaseAddress = BaseAddress };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15)) {
            if (_process.HasExited) {
                throw new InvalidOperationException(
                    $"Proxy server exited with code {_process.ExitCode}.\n{_output}\n{_errors}");
            }

            try {
                using var response = await client.GetAsync("health");
                if (response.IsSuccessStatusCode) {
                    return;
                }
            }
            catch (HttpRequestException) {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Proxy server did not become healthy.\n{_output}\n{_errors}");
    }

    private async Task StopServerAsync() {
        if (_process is not null) {
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync();
            _process.Dispose();
            _process = null;
        }
    }

    private static void EnsurePortIsAvailable() {
        try {
            using var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback,
                ProxyProtocol.Port);
            listener.Start();
            listener.Stop();
        }
        catch (System.Net.Sockets.SocketException ex) {
            throw new InvalidOperationException(
                $"TCP port {ProxyProtocol.Port} is occupied. Stop the running proxy before executing HTTP tests.",
                ex);
        }
    }

    private static string FindRepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "Antilatency.DisplayStylus.Proxy.sln"))) {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void Append(StringBuilder buffer, string? value) {
        if (value is null) {
            return;
        }
        lock (buffer) {
            buffer.AppendLine(value);
        }
    }

    private static string Snapshot(StringBuilder buffer) {
        lock (buffer) {
            return buffer.ToString();
        }
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);
