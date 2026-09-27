namespace DemoToolAPI_1
{
    public class WeatherForecast
    {
        public DateOnly Date { get; set; }

        /// <summary>The requested location, or null when none was given.</summary>
        public string? Location { get; set; }

        public int TemperatureC { get; set; }

        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

        public string? Summary { get; set; }
    }
}
