using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services.Egress
{
    /// <summary>One line of the egress proxy's JSON access log (see egress/envoy.yaml).</summary>
    /// <param name="RequestId">Matches the policy decision recorded for the connection.</param>
    /// <param name="BytesSent">Bytes the client sent upstream.</param>
    /// <param name="BytesReceived">Bytes the client received.</param>
    /// <param name="DurationMs">How long the connection lasted.</param>
    public sealed record EgressAccessLogLine(string RequestId, long? BytesSent, long? BytesReceived, long? DurationMs)
    {
        /// <summary>Parses a line, or null when it isn't one (blank, not JSON, or no request id).</summary>
        public static EgressAccessLogLine? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var requestId = Text(root, "request_id");
                if (string.IsNullOrEmpty(requestId) || requestId == "-")
                {
                    return null;
                }

                return new EgressAccessLogLine(requestId, Number(root, "bytes_from_client"), Number(root, "bytes_to_client"), Number(root, "duration_ms"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // Envoy writes numbers as numbers, or as strings ("-" when unknown) depending on the field and version.
        private static long? Number(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                return number;
            return null;
        }
    }

    /// <summary>
    /// Follows the egress proxy's access log (Egress:AccessLogPath) and adds each finished connection's traffic
    /// figures to the decision recorded for it. Policy decisions are made when a connection starts, so this is the
    /// only place sizes and durations come from; a large upload to an allowed host still shows up here.
    /// </summary>
    public sealed class EgressAccessLogReader : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private readonly IActivityStore _store;
        private readonly EgressOptions _options;
        private readonly string _path;
        private readonly ILogger<EgressAccessLogReader> _logger;
        private long _position;
        private byte[] _head = Array.Empty<byte>();

        public EgressAccessLogReader(IActivityStore store, IOptions<EgressOptions> options, IWebHostEnvironment environment,
            ILogger<EgressAccessLogReader> logger)
        {
            _store = store;
            _options = options.Value;
            _logger = logger;
            _path = string.IsNullOrWhiteSpace(_options.AccessLogPath) || Path.IsPathRooted(_options.AccessLogPath)
                ? _options.AccessLogPath
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.AccessLogPath));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.Mode == EgressMode.Off || string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            _logger.LogInformation("Reading egress traffic from {Path}.", _path);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ReadNewLines();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Couldn't read the egress access log yet.");
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>Applies every complete line added since the last read. Updates are idempotent, so re-reading is harmless.</summary>
        internal int ReadNewLines()
        {
            if (!File.Exists(_path))
            {
                return 0;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = ReadHead(stream);
            if (stream.Length < _position || !head.AsSpan().StartsWith(_head.AsSpan(0, Math.Min(_head.Length, head.Length))))
            {
                // The log was truncated or replaced (it's shorter, or starts differently): start again from the top.
                _position = 0;
            }

            _head = head;

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                return 0;
            }

            // Only whole lines; a line still being written is picked up next time.
            _position += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNewline + 1));
            var updated = 0;
            foreach (var line in text[..lastNewline].Split('\n'))
            {
                if (EgressAccessLogLine.Parse(line.TrimEnd('\r')) is EgressAccessLogLine entry
                    && _store.UpdateEgressTraffic(entry.RequestId, entry.BytesSent, entry.BytesReceived, entry.DurationMs))
                {
                    updated++;
                }
            }

            return updated;
        }

        // The first bytes of the file, to notice when it has been replaced by another of at least the same length.
        private static byte[] ReadHead(FileStream stream)
        {
            var buffer = new byte[Math.Min(256, stream.Length)];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            return buffer;
        }
    }
}
