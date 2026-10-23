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
        private readonly LiveScanner _liveScanner;

        public ToolsController(DummyResponseService dummyResponseService, LiveScanner liveScanner)
        {
            _dummyResponseService = dummyResponseService;
            _liveScanner = liveScanner;
        }

        /// <summary>
        /// Scans a given URL and returns a list of all links found on the site. Returns a canned response unless
        /// Scanner:LiveFetch is on, in which case the page is fetched through the egress proxy using the tool call's
        /// egress token (the X-Egress-Token header).
        /// </summary>
        /// <param name="url">The URL to scan</param>
        /// <param name="cancellationToken">Cancelled when the caller gives up</param>
        /// <returns>ScannerResponse with links found or error message</returns>
        [HttpGet("simple-scanner")]
        public async Task<IActionResult> SimpleScanner([FromQuery] string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new ScannerResponse
                {
                    Status = "error",
                    Message = "URL parameter is required"
                });
            }

            if (_liveScanner.Enabled)
            {
                var token = Request.Headers[LiveScanner.EgressTokenHeader].ToString();
                return Ok(await _liveScanner.ScanAsync(url, string.IsNullOrEmpty(token) ? null : token, cancellationToken));
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
