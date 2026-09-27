using ScanHarness.Services;
using Xunit;

namespace ScanHarness.Tests
{
    public class JsonFormatterTests
    {
        [Fact]
        public void Pretty_indents_compact_json()
        {
            var input = "{\"status\":\"success\",\"links\":[{\"text\":\"Home\",\"url\":\"/\"}]}";

            var result = JsonFormatter.Pretty(input);

            // Indented output spans multiple lines and uses spaces for nesting.
            Assert.Contains("\n", result);
            Assert.Contains("  \"status\": \"success\"", result);
            Assert.Contains("\"links\": [", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Pretty_returns_empty_for_blank_input(string? input)
        {
            Assert.Equal(string.Empty, JsonFormatter.Pretty(input));
        }

        [Fact]
        public void Pretty_returns_non_json_unchanged()
        {
            const string input = "not json at all";

            Assert.Equal(input, JsonFormatter.Pretty(input));
        }

        [Fact]
        public void Pretty_is_idempotent_on_already_indented_json()
        {
            var once = JsonFormatter.Pretty("{\"a\":1,\"b\":2}");
            var twice = JsonFormatter.Pretty(once);

            Assert.Equal(once, twice);
        }
    }
}
