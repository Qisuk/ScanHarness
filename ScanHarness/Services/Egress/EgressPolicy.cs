using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ScanHarness.Services.Engagements;

namespace ScanHarness.Services.Egress
{
    /// <summary>A connection the egress proxy has been asked to make.</summary>
    /// <param name="Method">The HTTP method; CONNECT for an HTTPS tunnel.</param>
    /// <param name="Target">The CONNECT target ("host:port") or the Host of a plain HTTP request.</param>
    /// <param name="ProxyAuthorization">The client's Proxy-Authorization header, which carries the egress token.</param>
    /// <param name="RequestId">The proxy's request id, used to match the decision with its access log line.</param>
    public sealed record EgressRequest(string Method, string Target, string? ProxyAuthorization, string? RequestId = null);

    /// <summary>The policy's answer for one connection.</summary>
    /// <param name="Allowed">Whether the proxy may connect.</param>
    /// <param name="Status">HTTP status for the proxy to use: 200 allowed, 403 denied, 407 needs an egress token.</param>
    /// <param name="Reason">Why, in words, e.g. "inside engagement Acme Ltd v2" or "no rule allows evil.example".</param>
    /// <param name="Rule">What decided it: a rule id, "engagement", "tool", or a built-in check such as "builtin:port".</param>
    /// <param name="Target">The parsed target, or null when it couldn't be parsed.</param>
    /// <param name="Upstream">The checked address to connect to ("ip:port"); only when allowed.</param>
    /// <param name="Caller">Who asked: a tool's function name, "harness:llm" or "anonymous".</param>
    /// <param name="Grant">The egress token's grant, when a valid one was presented.</param>
    public sealed record EgressDecision(bool Allowed, int Status, string Reason, string Rule, EgressTarget? Target, string? Upstream,
        string Caller, EgressGrant? Grant)
    {
        /// <summary>Rule ids for the checks built into the policy (rather than written in egress.json).</summary>
        public static class Rules
        {
            public const string Engagement = "engagement";
            public const string Tool = "tool";
            public const string Target = "builtin:target";
            public const string Token = "builtin:token";
            public const string Port = "builtin:port";
            public const string NoRule = "builtin:no-rule";
            public const string NonPublic = "builtin:non-public-address";
            public const string Resolve = "builtin:resolve";
            public const string Window = "builtin:engagement-window";
        }
    }

    /// <summary>
    /// Decides whether the egress proxy may make a connection. Everything is denied unless something allows it:
    /// the engagement in the caller's egress token (inside its testing window), the tool's own egress block, or an
    /// Allow rule in egress.json. Deny rules always win, the port must be allowed, and the host is resolved here and
    /// refused if any address isn't public; the proxy then connects to the exact address that was checked, so a
    /// name can't be re-pointed between the check and the connection (DNS rebinding).
    /// </summary>
    public sealed class EgressPolicy
    {
        private readonly Func<EgressPolicyDefinition> _definition;
        private readonly EgressTokens _tokens;
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
        private readonly TimeProvider _time;

        /// <param name="definition">The rules to apply; read on every decision so a reloaded policy takes effect.</param>
        /// <param name="tokens">Reads the egress tokens tools present.</param>
        /// <param name="resolve">Resolves a host name; defaults to DNS (tests pass their own).</param>
        /// <param name="time">The clock, for token expiry and testing windows.</param>
        public EgressPolicy(Func<EgressPolicyDefinition> definition, EgressTokens tokens,
            Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeProvider? time = null)
        {
            _definition = definition;
            _tokens = tokens;
            _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
            _time = time ?? TimeProvider.System;
        }

        public async Task<EgressDecision> DecideAsync(EgressRequest request, CancellationToken cancellationToken = default)
        {
            var policy = _definition();
            var now = _time.GetUtcNow().UtcDateTime;
            var isConnect = string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase);

            // Who is asking. A missing token falls back to rules that apply to everyone; a bad one is refused outright.
            var token = EgressTokens.FromProxyAuthorization(request.ProxyAuthorization);
            EgressGrant? grant = null;
            if (token != null)
            {
                grant = _tokens.Read(token, now, out var problem);
                if (grant == null)
                    return Deny(403, $"Refused: {problem}.", EgressDecision.Rules.Token, null, EgressCallers.Anonymous, null);
            }

            var caller = grant?.Caller ?? EgressCallers.Anonymous;
            var target = EgressHost.ParseTarget(request.Target, isConnect ? null : 80);
            if (target == null)
                return Deny(403, $"Refused: \"{request.Target}\" isn't a valid host and port.", EgressDecision.Rules.Target, null, caller, grant);

            var ports = grant?.Ports ?? policy.AllowedPorts;
            if (!ports.Contains(target.Port))
                return Deny(403, $"Refused: port {target.Port} isn't allowed (allowed: {string.Join(", ", ports)}).", EgressDecision.Rules.Port, target, caller, grant);

            var rules = policy.Rules.Where(r => r.AppliesToCaller(caller)).ToList();
            var deny = rules.FirstOrDefault(r => r.Action == EgressAction.Deny && !target.IsAddress && r.MatchesHost(target.Host))
                ?? rules.FirstOrDefault(r => r.Action == EgressAction.Deny && target.Address is IPAddress a && r.MatchesAddress(a));
            if (deny != null)
                return Deny(403, $"Refused by rule '{deny.Id}' ({deny.MatchDescription}).", deny.Id, target, caller, grant);

            var (allowedBy, reason, windowProblem) = FindAllow(target, grant, rules, DateOnly.FromDateTime(now));
            if (allowedBy == null)
            {
                if (windowProblem != null)
                    return Deny(403, windowProblem, EgressDecision.Rules.Window, target, caller, grant);
                // A client that sent no token gets a challenge, so a proxy-aware client retries with its credentials.
                return token == null
                    ? Deny(407, $"Refused: no egress token was presented and no rule lets anyone reach {target.Host}.", EgressDecision.Rules.Token, target, caller, null)
                    : Deny(403, $"Refused: nothing allows {caller} to reach {target.Host}.", EgressDecision.Rules.NoRule, target, caller, grant);
            }

            // Check where the name really points, and connect only to an address that passed.
            IPAddress[] addresses;
            if (target.Address != null)
            {
                addresses = new[] { target.Address };
            }
            else
            {
                try
                {
                    addresses = await _resolve(target.Host, cancellationToken);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    return Deny(403, $"Refused: {target.Host} couldn't be resolved ({ex.Message}).", EgressDecision.Rules.Resolve, target, caller, grant);
                }
            }

            if (addresses.Length == 0)
                return Deny(403, $"Refused: {target.Host} has no addresses.", EgressDecision.Rules.Resolve, target, caller, grant);

            foreach (var address in addresses)
            {
                var denyAddress = rules.FirstOrDefault(r => r.Action == EgressAction.Deny && r.MatchesAddress(address));
                if (denyAddress != null)
                    return Deny(403, $"Refused by rule '{denyAddress.Id}': {target.Host} resolves to {address}.", denyAddress.Id, target, caller, grant);

                var explicitlyAllowed = rules.Any(r => r.Action == EgressAction.Allow && r.MatchesAddress(address));
                if (policy.BlockNonPublicAddresses && EgressHost.IsNonPublic(address) && !explicitlyAllowed)
                    return Deny(403, $"Refused: {target.Host} resolves to {EgressHost.Canonical(address)}, which isn't a public address.",
                        EgressDecision.Rules.NonPublic, target, caller, grant);
            }

            // Prefer IPv4: the proxy's network may not route IPv6.
            var chosen = EgressHost.Canonical(addresses.OrderBy(a => EgressHost.Canonical(a).AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First());
            return new EgressDecision(true, 200, reason!, allowedBy, target, EgressHost.Join(chosen.ToString(), target.Port), caller, grant);
        }

        // The first thing that allows the target: the engagement, the tool's own domains, then egress.json rules.
        private static (string? Rule, string? Reason, string? WindowProblem) FindAllow(EgressTarget target, EgressGrant? grant,
            IReadOnlyList<EgressRule> rules, DateOnly today)
        {
            string? windowProblem = null;
            if (!target.IsAddress && grant?.EngagementDomains is { Count: > 0 } domains && domains.Any(d => ScopeGuard.IsWithin(target.Host, d)))
            {
                if (grant.WindowStart is DateOnly start && grant.WindowEnd is DateOnly end && today >= start && today <= end)
                    return (EgressDecision.Rules.Engagement, $"Allowed: inside engagement {grant.EngagementLabel}.", null);
                windowProblem = $"Refused: {target.Host} is in engagement {grant.EngagementLabel}, but today " +
                                $"({today.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}) is outside its testing window.";
            }

            if (!target.IsAddress && grant?.Domains?.FirstOrDefault(d => ScopeGuard.IsWithin(target.Host, d)) is string domain)
                return (EgressDecision.Rules.Tool, $"Allowed: {domain} is in {grant.Caller}'s egress list.", null);

            var rule = rules.FirstOrDefault(r => r.Action == EgressAction.Allow &&
                                                 (target.Address is IPAddress a ? r.MatchesAddress(a) : r.MatchesHost(target.Host)));
            if (rule != null)
                return (rule.Id, $"Allowed by rule '{rule.Id}' ({rule.MatchDescription}).", null);

            return (null, null, windowProblem);
        }

        private static EgressDecision Deny(int status, string reason, string rule, EgressTarget? target, string caller, EgressGrant? grant) =>
            new(false, status, reason, rule, target, null, caller, grant);
    }
}
