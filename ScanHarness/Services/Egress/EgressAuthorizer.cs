using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services.Egress
{
    /// <summary>
    /// The policy endpoint the egress proxy calls before every connection (POST /egress/authorize). It checks the
    /// proxy's shared key, asks <see cref="EgressPolicy"/>, records the attempt (database and
    /// <c>logs/egress-&lt;date&gt;.log</c>) and answers with the status for the proxy to use and, when allowed, the
    /// checked address to connect to. Anything other than a 200 means the proxy refuses the connection.
    /// </summary>
    public sealed class EgressAuthorizer
    {
        /// <summary>Request headers the proxy sends (see egress/envoy.yaml).</summary>
        public static class Headers
        {
            public const string Key = "x-egress-key";
            public const string Method = "x-egress-method";
            public const string Target = "x-egress-target";
            public const string Authorization = "x-egress-auth";
            public const string RequestId = "x-egress-request-id";

            /// <summary>Response header with the checked "ip:port" the proxy must connect to.</summary>
            public const string Upstream = "x-egress-upstream";
        }

        /// <summary>The Proxy-Authenticate challenge sent with a 407, so proxy-aware clients retry with their token.</summary>
        public const string Challenge = "Basic realm=\"scanharness-egress\"";

        private readonly EgressPolicy _policy;
        private readonly IActivityStore? _store;
        private readonly EgressOptions _options;
        private readonly ILogger<EgressAuthorizer>? _logger;
        private readonly string _logDirectory;
        private readonly object _fileGate = new();

        /// <param name="store">Where attempts are recorded; null records them only in the text log.</param>
        /// <param name="logDirectory">Folder for the text log; defaults to logs/ next to the app.</param>
        public EgressAuthorizer(EgressPolicy policy, IOptions<EgressOptions> options, IActivityStore? store = null,
            ILogger<EgressAuthorizer>? logger = null, string? logDirectory = null)
        {
            _policy = policy;
            _options = options.Value;
            _store = store;
            _logger = logger;
            _logDirectory = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        }

        /// <summary>Handles one policy check from the proxy.</summary>
        public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (_options.Mode == EgressMode.Off)
            {
                return Results.NotFound();
            }

            if (!KeyMatches(request.Headers[Headers.Key].ToString()))
            {
                _logger?.LogWarning("Refused an egress policy check without the right key from {Remote}.", request.HttpContext.Connection.RemoteIpAddress);
                return Results.Unauthorized();
            }

            var egressRequest = new EgressRequest(
                request.Headers[Headers.Method].ToString(),
                request.Headers[Headers.Target].ToString(),
                NullIfEmpty(request.Headers[Headers.Authorization].ToString()),
                NullIfEmpty(request.Headers[Headers.RequestId].ToString()));

            var decision = await _policy.DecideAsync(egressRequest, cancellationToken);
            Record(egressRequest, decision);

            var response = request.HttpContext.Response;
            if (decision.Allowed)
            {
                response.Headers[Headers.Upstream] = decision.Upstream;
            }
            else if (decision.Status == 407)
            {
                response.Headers.ProxyAuthenticate = Challenge;
            }

            return Results.Text(decision.Reason, "text/plain", Encoding.UTF8, decision.Status);
        }

        /// <summary>Turns a decision into the recorded event.</summary>
        public static EgressEvent ToEvent(EgressRequest request, EgressDecision decision) => new()
        {
            RequestId = request.RequestId,
            Method = string.IsNullOrEmpty(request.Method) ? "?" : request.Method.ToUpperInvariant(),
            Target = request.Target,
            Host = decision.Target?.Host,
            Port = decision.Target?.Port,
            Upstream = decision.Upstream,
            Allowed = decision.Allowed,
            Status = decision.Status,
            Rule = decision.Rule,
            Reason = decision.Reason,
            Caller = decision.Caller,
            ToolCallId = decision.Grant?.ToolCallId,
            SessionId = decision.Grant?.SessionId,
            UserName = decision.Grant?.UserName,
            EngagementId = decision.Grant?.EngagementId,
        };

        // Recording is best-effort: a storage failure must not change the decision, which has already been made.
        private void Record(EgressRequest request, EgressDecision decision)
        {
            var egressEvent = ToEvent(request, decision);
            try
            {
                _store?.SaveEgressEvent(egressEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not save egress event {Id}.", egressEvent.Id);
            }

            // Tab-separated: timestamp, decision, status, caller, user, session, tool call id, method, target, upstream, rule, reason.
            static string Column(string? value) => string.IsNullOrEmpty(value) ? "-" : LogService.OneLine(value);
            var line = string.Join('\t', egressEvent.Timestamp.ToString("O"), egressEvent.Allowed ? "Allowed" : "Refused",
                egressEvent.Status, Column(egressEvent.Caller), Column(egressEvent.UserName), Column(egressEvent.SessionId),
                Column(egressEvent.ToolCallId), Column(egressEvent.Method), Column(egressEvent.Target), Column(egressEvent.Upstream),
                Column(egressEvent.Rule), Column(egressEvent.Reason));
            try
            {
                lock (_fileGate)
                {
                    Directory.CreateDirectory(_logDirectory);
                    File.AppendAllText(Path.Combine(_logDirectory, $"egress-{egressEvent.Timestamp:yyyy-MM-dd}.log"), line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not write the egress log.");
            }
        }

        private bool KeyMatches(string presented)
        {
            var expected = Encoding.UTF8.GetBytes(_options.PolicyKey);
            var actual = Encoding.UTF8.GetBytes(presented);
            // FixedTimeEquals returns false for different lengths without leaking where they differ.
            return expected.Length >= EgressOptions.MinimumPolicyKeyLength && CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
