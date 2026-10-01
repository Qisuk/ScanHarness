using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ScanHarness.Services.Llm
{
    /// <summary>Raised when an OpenAI-compatible endpoint returns an error response.</summary>
    public class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    /// <summary>
    /// Minimal client for the OpenAI-compatible REST API: lists models and creates chat
    /// completions. Requests and responses are handled as JSON nodes so provider-specific
    /// fields pass through untouched.
    /// </summary>
    public class OpenAiCompatibleClient
    {
        private readonly HttpClient _http;

        /// <param name="http">An <see cref="HttpClient"/> prepared with <see cref="Configure"/>.</param>
        public OpenAiCompatibleClient(HttpClient http)
        {
            _http = http;
        }

        /// <summary>The API base URL this client talks to.</summary>
        public Uri? BaseAddress => _http.BaseAddress;

        /// <summary>Applies base URL, timeout and API key from <paramref name="options"/> to <paramref name="http"/>.</summary>
        public static void Configure(HttpClient http, LlmOptions options)
        {
            // A trailing slash makes relative paths ("models") resolve under /v1 rather than replacing it.
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }
        }

        /// <summary>Returns the ids of the models the endpoint serves (<c>GET /models</c>).</summary>
        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            using var response = await _http.GetAsync("models", cancellationToken);
            var body = await ReadSuccessBodyAsync(response, cancellationToken);

            return body["data"]?.AsArray()
                .Select(m => m?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        /// <summary>
        /// Sends a chat completion request (<c>POST /chat/completions</c>) and returns the response body, with any
        /// reasoning gathered into one field and think blocks removed from the answer (see <see cref="LlmReasoning"/>).
        /// </summary>
        public async Task<JsonObject> CreateChatCompletionAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("chat/completions", content, cancellationToken);
            var body = await ReadSuccessBodyAsync(response, cancellationToken);
            LlmReasoning.Normalise(body);
            return body;
        }

        private static async Task<JsonObject> ReadSuccessBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException($"LLM endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(text)}");
            }

            return JsonNode.Parse(text) as JsonObject
                ?? throw new LlmException("LLM endpoint returned a response that is not a JSON object.");
        }

        // OpenAI-style errors look like {"error":{"message":"..."}}, Ollama's native ones like
        // {"error":"..."}; fall back to a trimmed raw body.
        private static string ExtractErrorMessage(string body)
        {
            try
            {
                var error = JsonNode.Parse(body)?["error"];
                var message = error is JsonValue ? error.GetValue<string>() : error?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch (Exception)
            {
                // Not JSON; use the raw body below.
            }

            return body.Length > 300 ? body[..300] + "…" : body;
        }
    }
}
