using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ScanHarness.UITests;

/// <summary>
/// A minimal OpenAI-compatible endpoint serving one reasoning model, so UI tests can drive an LLM brain
/// deterministically. Decisions choose the weather tool for the place named in the prompt; summaries
/// describe the outcome. Every answer includes reasoning: decisions in a <c>reasoning</c> field (as Ollama
/// returns it) and summaries in a <c>&lt;think&gt;</c> block (as some models write it).
/// </summary>
public sealed class FakeLlmServer : IDisposable
{
    public const string Model = "fake-reasoner";
    public const string DecisionReasoning = "The user wants a forecast, and named the place in their message.";
    public const string SummaryReasoning = "The tool call failed, so there is no forecast to report.";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeLlmServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>Base URL for the harness's <c>Llm:BaseUrl</c>.</summary>
    public string BaseUrl => $"http://localhost:{Port}/v1";

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            try
            {
                var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                var reply = context.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                    ? new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = Model }) }
                    : Complete(JsonNode.Parse(body)!.AsObject());
                var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static JsonObject Complete(JsonObject request)
    {
        var prompt = request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        var isDecision = request["response_format"]?["json_schema"]?["name"]?.GetValue<string>() == "tool_decision";

        var message = new JsonObject { ["role"] = "assistant" };
        if (isDecision)
        {
            var place = prompt.Split(' ').Last().TrimEnd('?', '.', '!');
            message["content"] = new JsonObject
            {
                ["tool"] = "weather_forecast", ["url"] = null, ["location"] = place, ["message"] = $"I'll get the weather for {place}.",
            }.ToJsonString();
            message["reasoning"] = DecisionReasoning;
        }
        else
        {
            message["content"] = $"<think>{SummaryReasoning}</think>The weather tool failed, so there is no forecast.";
        }

        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
