using Microsoft.AspNetCore.Mvc;
using DemoToolAPI_1.Models;
using DemoToolAPI_1.Services;

namespace DemoToolAPI_1.Controllers
{
    /// <summary>
    /// Controller for demo tool APIs
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ToolsController : ControllerBase
    {
        private readonly DummyResponseService _dummyResponseService;

        public ToolsController(DummyResponseService dummyResponseService)
        {
            _dummyResponseService = dummyResponseService;
        }

        /// <summary>
        /// Scans a given URL and returns a list of all links found on the site
        /// </summary>
        /// <param name="url">The URL to scan</param>
        /// <returns>ScannerResponse with links found or error message</returns>
        [HttpGet("simple-scanner")]
        public IActionResult SimpleScanner([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new ScannerResponse
                {
                    Status = "error",
                    Message = "URL parameter is required"
                });
            }

            var response = _dummyResponseService.GetRandomScannerResponse();
            return Ok(response);
        }

        /// <summary>
        /// Performs a simulated attack on a given URL
        /// </summary>
        /// <param name="url">The URL to attack</param>
        /// <returns>AttackResponse with attack simulation results</returns>
        [HttpPost("attack")]
        public IActionResult Attack([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new AttackResponse
                {
                    Status = "error",
                    Message = "URL parameter is required"
                });
            }

            var response = _dummyResponseService.GetRandomAttackResponse();
            return Ok(response);
        }
    }
}
