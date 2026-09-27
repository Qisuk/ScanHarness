using System.Diagnostics;
using NUnit.Framework;

namespace ScanHarness.UITests;

/// <summary>
/// Starts the ScanHarness app once for the whole UI test run and shuts it down afterward.
/// The app is launched as a real Kestrel process so Playwright can drive it over HTTP.
/// </summary>
[SetUpFixture]
public class ServerFixture
{
    private const string Host = "http://127.0.0.1:5290";
    private Process? _process;

    public static string BaseUrl => Host;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var appDll = LocateAppDll();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{appDll}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        _process.StartInfo.Environment["ASPNETCORE_URLS"] = Host;
        _process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        _process.Start();

        // Drain stdout/stderr (so the app never blocks on a full pipe buffer) and tee to a log file for debugging.
        var logPath = Path.Combine(Path.GetTempPath(), "scanharness-uitest-server.log");
        File.WriteAllText(logPath, "");
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(logPath, e.Data + Environment.NewLine); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(logPath, "ERR " + e.Data + Environment.NewLine); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForServerAsync();
    }

    [OneTimeTearDown]
    public void StopServer()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process?.Dispose();
    }

    private static string LocateAppDll()
    {
        // Walk up from the test output directory until we find the app project, then take its newest build output.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScanHarness", "ScanHarness.csproj")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new FileNotFoundException("Could not locate the ScanHarness app project from the test output directory.");
        }

        var binDir = Path.Combine(dir.FullName, "ScanHarness", "bin");
        var dll = Directory.GetFiles(binDir, "ScanHarness.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (dll == null)
        {
            throw new FileNotFoundException($"Built ScanHarness.dll not found under {binDir}. Build the solution first.");
        }

        return dll;
    }

    private static async Task WaitForServerAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(Host + "/");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Server not ready yet.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"ScanHarness app did not start listening on {Host} within 60s.");
    }
}
