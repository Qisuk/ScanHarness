using System.Net;
using ScanHarness.Services.Llm;

namespace ScanHarness.Services.Egress
{
    /// <summary>HTTP handlers for the harness's own outgoing traffic.</summary>
    public static class EgressHandlers
    {
        /// <summary>
        /// The handler for the LLM client. With Egress:RouteLlmTraffic on (and egress not Off), calls to a non-loopback
        /// endpoint go through the egress proxy as caller "harness:llm", so prompts leaving for a hosted model are
        /// checked against egress.json and recorded like tool traffic. A loopback endpoint (a local Ollama) is called
        /// directly: the proxy runs in a container and couldn't reach it, and nothing leaves the machine.
        /// </summary>
        /// <param name="lifetime">How long the handler is used; its token must outlive it, so it lasts at least twice as long.</param>
        public static HttpMessageHandler ForLlm(EgressOptions egress, LlmOptions llm, EgressTokens tokens, TimeSpan lifetime)
        {
            var handler = new SocketsHttpHandler();
            if (!RoutesLlmTraffic(egress, llm))
            {
                return handler;
            }

            var seconds = Math.Max(egress.TokenLifetimeSeconds, lifetime.TotalSeconds * 2);
            var token = tokens.Mint(new EgressGrant(EgressCallers.HarnessLlm, DateTime.UtcNow.AddSeconds(seconds)));
            handler.Proxy = new WebProxy(egress.ProxyUrl) { Credentials = new NetworkCredential("egress", token) };
            handler.UseProxy = true;
            return handler;
        }

        /// <summary>Whether the LLM client's traffic goes through the egress proxy.</summary>
        public static bool RoutesLlmTraffic(EgressOptions egress, LlmOptions llm) =>
            egress.Mode != EgressMode.Off && egress.RouteLlmTraffic
            && Uri.TryCreate(llm.BaseUrl, UriKind.Absolute, out var endpoint) && !endpoint.IsLoopback;
    }
}
