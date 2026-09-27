using Microsoft.AspNetCore.Mvc.Testing;

namespace DemoToolAPI_1.Tests
{
    /// <summary>
    /// Custom WebApplicationFactory for integration tests
    /// </summary>
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Configure test-specific services if needed
            base.ConfigureWebHost(builder);
        }
    }
}
