using Microsoft.AspNetCore.Mvc;

namespace DemoToolAPI_1.Controllers
{
    /// <summary>
    /// Simulated weather forecast used as the harness "weather" tool. Returns random
    /// forecasts; the location is only echoed back, it doesn't change the data.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        private static readonly string[] Summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];

        /// <summary>Returns a simulated five-day forecast.</summary>
        /// <param name="location">Optional place name, echoed in each forecast (e.g. "London").</param>
        [HttpGet(Name = "GetWeatherForecast")]
        public IEnumerable<WeatherForecast> Get([FromQuery] string? location = null)
        {
            var place = string.IsNullOrWhiteSpace(location) ? null : location.Trim();

            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                Location = place,
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }
    }
}
