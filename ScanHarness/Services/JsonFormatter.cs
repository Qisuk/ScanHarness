using System.Text.Json;

namespace ScanHarness.Services
{
    /// <summary>Helpers for presenting tool results in the UI.</summary>
    public static class JsonFormatter
    {
        private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

        /// <summary>
        /// Returns an indented (pretty-printed) form of <paramref name="raw"/> when it is valid JSON;
        /// otherwise returns the input unchanged. Null/whitespace yields an empty string.
        /// </summary>
        public static string Pretty(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(doc.RootElement, IndentedOptions);
            }
            catch (JsonException)
            {
                return raw;
            }
        }
    }
}
