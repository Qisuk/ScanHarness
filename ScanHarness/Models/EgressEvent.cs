namespace ScanHarness.Models
{
    /// <summary>
    /// One connection the egress proxy was asked to make, and the policy's decision. Recorded for every attempt,
    /// allowed or refused, so all traffic in and out of the tools can be audited. Traffic figures arrive later
    /// from the proxy's access log, when it's configured.
    /// </summary>
    public class EgressEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>The proxy's request id, which matches the decision with its access log line.</summary>
        public string? RequestId { get; set; }

        /// <summary>CONNECT for an HTTPS tunnel, otherwise the plain HTTP method.</summary>
        public string Method { get; set; } = string.Empty;

        /// <summary>What the client asked for, as sent ("host:port" or a Host).</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>The normalised host, when the target could be parsed.</summary>
        public string? Host { get; set; }

        public int? Port { get; set; }

        /// <summary>The checked address the proxy connected to ("ip:port"); only for allowed connections.</summary>
        public string? Upstream { get; set; }

        public bool Allowed { get; set; }

        /// <summary>200 allowed, 403 refused, 407 refused for want of an egress token.</summary>
        public int Status { get; set; }

        /// <summary>What decided it: a rule id from egress.json, "engagement", "tool" or a built-in check.</summary>
        public string Rule { get; set; } = string.Empty;

        /// <summary>Why, in words.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>A tool's function name, "harness:llm" or "anonymous".</summary>
        public string Caller { get; set; } = string.Empty;

        /// <summary>The tool call the connection belongs to, from its egress token.</summary>
        public string? ToolCallId { get; set; }

        public string? SessionId { get; set; }

        public string? UserName { get; set; }

        public string? EngagementId { get; set; }

        /// <summary>Bytes the client sent upstream (from the access log).</summary>
        public long? BytesSent { get; set; }

        /// <summary>Bytes received from upstream (from the access log).</summary>
        public long? BytesReceived { get; set; }

        /// <summary>How long the connection lasted (from the access log).</summary>
        public long? DurationMs { get; set; }

        /// <summary>One line describing the attempt, e.g. "CONNECT shop.acme.test:443 → 93.184.215.14:443".</summary>
        public string Describe() =>
            Upstream == null ? $"{Method} {Target}" : $"{Method} {Target} → {Upstream}";
    }
}
