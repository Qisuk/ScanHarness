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

    /// <summary>The fake OpenAI-compatible endpoint the app's LLM brains talk to.</summary>
    public static FakeLlmServer FakeLlm { get; } = new();

    private static readonly string DatabaseDirectory = Path.Combine(Path.GetTempPath(), "scanharness-uitests", Guid.NewGuid().ToString("N"));
    private static string DatabasePath => Path.Combine(DatabaseDirectory, "activity.db");

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
        // UI tests need deterministic decisions: the only LLM is a fake reasoning model (FakeLlmServer), never a real
        // endpoint, and the keyword brain stays the default.
        _process.StartInfo.Environment["Llm__Enabled"] = "true";
        _process.StartInfo.Environment["Llm__BaseUrl"] = FakeLlm.BaseUrl;
        _process.StartInfo.Environment["Llm__DefaultModel"] = "";
        _process.StartInfo.Environment["Llm__DefaultSummaryModel"] = "";
        // A fresh database per run, so tests start empty and never touch the real activity data.
        _process.StartInfo.Environment["Persistence__DatabasePath"] = DatabasePath;
        // Pin tool classifications so tests don't depend on appsettings.json. Attack is set to External
        // (its default is Internal) so the tests also show a config override reaching the UI.
        _process.StartInfo.Environment["Tools__Classifications__simple_scanner"] = "Internal";
        _process.StartInfo.Environment["Tools__Classifications__attack"] = "External";
        _process.StartInfo.Environment["Tools__Classifications__weather_forecast"] = "External";
        // Most tests scan example.com without an engagement; scope enforcement is covered by unit tests.
        _process.StartInfo.Environment["Engagements__EnforceScope"] = "false";
        _process.StartInfo.Environment["Engagements__VerifiedDomains__0"] = "acme.test";
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
        FakeLlm.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process?.Dispose();

        try
        {
            Directory.Delete(DatabaseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort clean-up of the temp database.
        }
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
