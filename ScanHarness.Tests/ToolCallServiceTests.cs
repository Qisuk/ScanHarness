using ScanHarness.Models;
using ScanHarness.Services;
using Xunit;

namespace ScanHarness.Tests
{
    public class ToolCallServiceTests
    {
        private const string Brain = "test-model (LLM)";

        private static (ToolCallService Service, LogService Log, FakeHttpMessageHandler Handler) Create(string json = "[]")
        {
            var handler = FakeHttpMessageHandler.Returning(json);
            var log = new LogService();
            return (new ToolCallService(new HttpClient(handler), log), log, handler);
        }

        [Fact]
        public async Task Weather_call_passes_the_location_to_the_forecast_api()
        {
            var (service, _, handler) = Create("[{\"date\":\"2026-09-28\",\"location\":\"New York\",\"temperatureC\":20}]");

            var call = await service.ExecuteToolAsync("weather", "forecast", new() { ["location"] = "New York" });

            Assert.Equal(ToolCallStatus.Completed, call.Status);
            var request = Assert.Single(handler.Requests).Request;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/WeatherForecast", request.RequestUri!.AbsolutePath);
            Assert.Equal("?location=New%20York", request.RequestUri.Query);
            Assert.Contains("New York", call.Result);
        }

        [Fact]
        public async Task Weather_call_without_a_location_sends_no_query()
        {
            var (service, _, handler) = Create();

            await service.ExecuteToolAsync("weather", "forecast", new());

            Assert.Equal("", handler.Requests[0].Request.RequestUri!.Query);
        }

        [Fact]
        public async Task Brain_is_recorded_on_the_call_and_every_log_entry()
        {
            var (service, log, _) = Create();

            var call = await service.ExecuteToolAsync("weather", "forecast", new(), brain: Brain);

            Assert.Equal(Brain, call.Brain);
            var entries = log.GetLogs().Where(e => e.ToolCallId == call.Id).ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.Equal(Brain, e.Brain));
        }

        [Fact]
        public async Task Approval_decisions_record_the_brain_that_chose_the_call()
        {
            var (service, log, _) = Create("{}");

            // ExecuteToolAsync runs synchronously up to the approval wait, so the call is pending once it returns.
            var pending = service.ExecuteToolAsync("attack", "execute", new() { ["url"] = "https://example.com" }, requiresApproval: true, brain: Brain);
            service.RejectToolCall(Assert.Single(service.GetToolCalls()).Id);
            var call = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ToolCallStatus.Cancelled, call.Status);
            var rejection = Assert.Single(log.GetLogs(), e => e.Message.StartsWith("Rejected tool call"));
            Assert.Equal(ScanHarness.Models.LogLevel.User, rejection.Level);
            Assert.Equal(Brain, rejection.Brain);
        }

        [Fact]
        public async Task Brain_is_optional()
        {
            var (service, log, _) = Create();

            var call = await service.ExecuteToolAsync("weather", "forecast", new());

            Assert.Null(call.Brain);
            Assert.All(log.GetLogs(), e => Assert.Null(e.Brain));
        }
    }
}
