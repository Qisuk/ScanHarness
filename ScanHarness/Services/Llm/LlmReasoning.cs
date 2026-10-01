using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanHarness.Services.Llm
{
    /// <summary>
    /// A model's reasoning ("thinking"), which providers return in different places: a <c>reasoning_content</c>
    /// field (DeepSeek, vLLM), a <c>reasoning</c> field (Ollama's OpenAI API), or <c>&lt;think&gt;…&lt;/think&gt;</c>
    /// blocks inside the answer. <see cref="Normalise"/> moves all of them into one field and strips the blocks from
    /// the answer, so the rest of the harness reads the answer as before and the reasoning in one place.
    /// Only reasoning models produce any; for others it is null.
    /// </summary>
    public static class LlmReasoning
    {
        /// <summary>Field the normalised reasoning is stored in on the first choice's message.</summary>
        public const string Field = "reasoning_content";

        /// <summary>Longest reasoning kept; the rest is cut so one answer can't flood the log.</summary>
        public const int MaxLength = 20_000;

        private static readonly Regex ThinkBlock = new(@"<think>(.*?)(?:</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <summary>Gathers the reasoning of the response's first choice into <see cref="Field"/> and removes think blocks from its content.</summary>
        public static void Normalise(JsonObject response)
        {
            if (response["choices"]?.AsArray().FirstOrDefault()?["message"] is not JsonObject message)
            {
                return;
            }

            var parts = new List<string>();
            AddText(parts, message[Field]);
            AddText(parts, message["reasoning"]);

            if (message["content"] is JsonValue content && content.TryGetValue<string>(out var text) && text.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match block in ThinkBlock.Matches(text))
                {
                    parts.Add(block.Groups[1].Value);
                }

                message["content"] = ThinkBlock.Replace(text, "").Trim();
            }

            var reasoning = string.Join("\n\n", parts.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct());
            message[Field] = reasoning.Length == 0 ? null : Limit(reasoning);
        }

        /// <summary>The reasoning <see cref="Normalise"/> found in a response, or null.</summary>
        public static string? Of(JsonObject response) =>
            response["choices"]?.AsArray().FirstOrDefault()?["message"]?[Field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                ? text
                : null;

        public static string Limit(string text) => text.Length <= MaxLength ? text : text[..MaxLength] + "… (truncated)";

        private static void AddText(List<string> parts, JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }
    }
}
