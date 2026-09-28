using ScanHarness.Services;
using Xunit;

namespace ScanHarness.Tests
{
    public class LogServiceTests
    {
        [Theory]
        [InlineData("scan example.com", "scan example.com")]
        [InlineData("line one\nline two", "line one\\nline two")]
        [InlineData("line one\r\nline two\rline three", "line one\\nline two\\nline three")]
        [InlineData("tab\there", "tab\\there")]
        [InlineData("a real \\n in text", "a real \\\\n in text")]
        public void OneLine_escapes_line_breaks_and_tabs_so_each_entry_stays_on_one_line(string message, string expected)
        {
            Assert.Equal(expected, LogService.OneLine(message));
        }

        [Fact]
        public void Multi_line_messages_are_kept_intact_in_memory()
        {
            var log = new LogService();

            log.LogUser("Request: scan example.com\nand list the links");

            Assert.Equal("Request: scan example.com\nand list the links", Assert.Single(log.GetLogs()).Message);
        }

        [Fact]
        public void Multi_line_request_becomes_a_single_line_session_title()
        {
            var session = new SessionState();

            session.RecordTitle("scan example.com\n\n  and list   the links");

            Assert.Equal("scan example.com and list the links", session.Current.Title);
        }
    }
}
