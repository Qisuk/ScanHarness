using System.Net;
using System.Text.Json;
using Xunit;

namespace DemoToolAPI_1.Tests
{
    public class WeatherForecastControllerTests : IAsyncLifetime
    {
        private CustomWebApplicationFactory _factory = null!;
        private HttpClient _client = null!;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public Task InitializeAsync()
        {
            _factory = new CustomWebApplicationFactory();
            _client = _factory.CreateClient();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        private async Task<WeatherForecast[]> GetForecastAsync(string query = "")
        {
            var response = await _client.GetAsync($"/WeatherForecast{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<WeatherForecast[]>(content, _jsonOptions)!;
        }

        [Fact]
        public async Task Get_ReturnsFiveDaysStartingTomorrow()
        {
            var forecasts = await GetForecastAsync();

            Assert.Equal(5, forecasts.Length);
            var tomorrow = DateOnly.FromDateTime(DateTime.Now.AddDays(1));
            Assert.Equal(Enumerable.Range(0, 5).Select(i => tomorrow.AddDays(i)), forecasts.Select(f => f.Date));
        }

        [Fact]
        public async Task Get_ReturnsPlausibleTemperaturesAndSummaries()
        {
            var forecasts = await GetForecastAsync();

            Assert.All(forecasts, f =>
            {
                Assert.InRange(f.TemperatureC, -20, 54);
                Assert.False(string.IsNullOrWhiteSpace(f.Summary));
            });
        }

        [Fact]
        public async Task Get_WithLocation_EchoesTheLocation()
        {
            var forecasts = await GetForecastAsync("?location=" + Uri.EscapeDataString("  New York "));

            Assert.All(forecasts, f => Assert.Equal("New York", f.Location));
        }

        [Theory]
        [InlineData("")]
        [InlineData("?location=")]
        [InlineData("?location=%20%20")]
        public async Task Get_WithoutLocation_LeavesLocationEmpty(string query)
        {
            var forecasts = await GetForecastAsync(query);

            Assert.All(forecasts, f => Assert.Null(f.Location));
        }
    }
}
