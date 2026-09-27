using System.Text.Json;
using DemoToolAPI_1.Models;

namespace DemoToolAPI_1.Services
{
    public class DummyResponseService
    {
        private readonly List<ScannerResponse> _scannerResponses;
        private readonly List<AttackResponse> _attackResponses;
        private readonly Random _random = new();

        public DummyResponseService(IWebHostEnvironment env)
        {
            var filePath = Path.Combine(env.ContentRootPath, "dummy_responses.json");
            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<DummyResponseData>(json, options)
                ?? throw new InvalidOperationException("Failed to load dummy responses");

            _scannerResponses = data.SimpleScannerResponses ?? new List<ScannerResponse>();
            _attackResponses = data.AttackResponses ?? new List<AttackResponse>();
        }

        public ScannerResponse GetRandomScannerResponse()
        {
            return _scannerResponses[_random.Next(_scannerResponses.Count)];
        }

        public AttackResponse GetRandomAttackResponse()
        {
            return _attackResponses[_random.Next(_attackResponses.Count)];
        }
    }

    public class DummyResponseData
    {
        public List<ScannerResponse>? SimpleScannerResponses { get; set; }
        public List<AttackResponse>? AttackResponses { get; set; }
    }
}
