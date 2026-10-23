using System.Net;
using System.Text.RegularExpressions;
using DemoToolAPI_1.Models;

namespace DemoToolAPI_1.Services
{
    /// <summary>Settings for the scanner's live mode (config section "Scanner").</summary>
    public class ScannerOptions
    {
        public const string SectionName = "Scanner";

        /// <summary>
        /// Fetch the page for real (through the egress proxy) instead of returning a canned response. Off by default:
        /// the demo tools never touch the network unless this is turned on, as it is in docker-compose's egress profile.
        /// </summary>
        public bool LiveFetch { get; set; }

        /// <summary>
        /// The forward proxy to use; empty uses the HTTPS_PROXY / HTTP_PROXY environment variables. In the egress
        /// profile the container has no other way out, so this is a convenience, not the control.
        /// </summary>
        public string ProxyUrl { get; set; } = string.Empty;

        /// <summary>How many of the page's links to follow (like a crawler would); 0 only fetches the page itself.</summary>
        public int FollowLinks { get; set; } = 5;

        /// <summary>Longest a single request may take.</summary>
        public int TimeoutSeconds { get; set; } = 10;
    }

    /// <summary>
    /// The scanner's live mode: fetches the page, lists its links, then follows a few of them the way a crawler
    /// would. Every request goes through the egress proxy carrying the tool call's egress token, so links outside
    /// the engagement are refused by the proxy, and each link says whether it could be reached.
    /// </summary>
    public class LiveScanner
    {
        /// <summary>Header carrying the tool call's egress token from the harness.</summary>
        public const string EgressTokenHeader = "X-Egress-Token";

        private const int MaxPageBytes = 512 * 1024;
        private const int MaxLinks = 50;

        private static readonly Regex Anchor = new(@"<a\s[^>]*?href\s*=\s*[""']([^""'#]+)[""'][^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static readonly Regex Tags = new("<[^>]+>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private readonly ScannerOptions _options;
        private readonly Func<HttpMessageHandler>? _handlerFactory;

        /// <param name="handlerFactory">Builds the HTTP handler (tests pass a fake); null uses the proxy settings.</param>
        public LiveScanner(Microsoft.Extensions.Options.IOptions<ScannerOptions> options, Func<HttpMessageHandler>? handlerFactory = null)
        {
            _options = options.Value;
            _handlerFactory = handlerFactory;
        }

        public bool Enabled => _options.LiveFetch;

        /// <summary>Scans <paramref name="url"/>, presenting <paramref name="egressToken"/> to the proxy.</summary>
        public async Task<ScannerResponse> ScanAsync(string url, string? egressToken, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                return new ScannerResponse { Status = "error", Message = $"\"{url}\" isn't an http(s) URL." };
            }

            using var client = CreateClient(egressToken);
            string html;
            try
            {
                using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (await RefusedAsync(response, cancellationToken) is string refusal)
                {
                    return new ScannerResponse { Status = "blocked", Message = $"The egress proxy refused {target.Host}: {refusal}" };
                }

                html = await ReadLimitedAsync(response, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new ScannerResponse { Status = "failure", Message = $"{target} answered {(int)response.StatusCode} {response.ReasonPhrase}." };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new ScannerResponse
                {
                    Status = IsProxyRefusal(ex) ? "blocked" : "error",
                    Message = IsProxyRefusal(ex) ? $"The egress proxy refused {target.Host}." : $"Couldn't fetch {target}: {ex.Message}",
                };
            }

            var links = ExtractLinks(html, target);
            var followed = 0;
            var refused = 0;
            // Like a crawler sampling a site's neighbourhood: the first link to each host, so links that leave the
            // site (and the engagement) get followed too.
            var sample = links.GroupBy(l => new Uri(l.Url).Host, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
            foreach (var link in sample.Take(Math.Max(0, _options.FollowLinks)))
            {
                link.Note = await ProbeAsync(client, link.Url, cancellationToken);
                followed++;
                if (link.Note.StartsWith("refused", StringComparison.Ordinal))
                    refused++;
            }

            var message = $"Fetched {target} and found {links.Count} link{(links.Count == 1 ? "" : "s")}.";
            if (followed > 0)
                message += $" Followed {followed}; the egress proxy refused {refused}.";
            return new ScannerResponse { Status = "success", Message = message, Links = links };
        }

        /// <summary>The page's absolute http(s) links, in order, without duplicates.</summary>
        public static List<LinkInfo> ExtractLinks(string html, Uri page)
        {
            var links = new List<LinkInfo>();
            try
            {
                foreach (Match match in Anchor.Matches(html))
                {
                    if (!Uri.TryCreate(page, WebUtility.HtmlDecode(match.Groups[1].Value.Trim()), out var url)
                        || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                        || links.Any(l => l.Url == url.AbsoluteUri))
                    {
                        continue;
                    }

                    var text = WebUtility.HtmlDecode(Tags.Replace(match.Groups[2].Value, " ")).Trim();
                    text = Regex.Replace(text, @"\s+", " ");
                    links.Add(new LinkInfo { LinkText = text.Length > 0 ? text : url.AbsoluteUri, Url = url.AbsoluteUri });
                    if (links.Count == MaxLinks)
                        break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A page built to make the pattern crawl keeps whatever links were found before the timeout.
            }

            return links;
        }

        // Follows one link: "reachable (200)", "refused by the egress proxy", or what went wrong.
        private static async Task<string> ProbeAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return await RefusedAsync(response, cancellationToken) is string refusal
                    ? $"refused by the egress proxy: {refusal}"
                    : $"reachable ({(int)response.StatusCode})";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return IsProxyRefusal(ex) ? "refused by the egress proxy" : $"error: {ex.Message}";
            }
        }

        private HttpClient CreateClient(string? egressToken)
        {
            HttpMessageHandler handler;
            if (_handlerFactory != null)
            {
                handler = _handlerFactory();
            }
            else
            {
                var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
                var proxyUrl = !string.IsNullOrWhiteSpace(_options.ProxyUrl) ? _options.ProxyUrl
                    : Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    // The token is the proxy password; the proxy asks for it (407) and the handler answers.
                    sockets.Proxy = new WebProxy(proxyUrl)
                    {
                        Credentials = egressToken == null ? null : new NetworkCredential("egress", egressToken),
                    };
                    sockets.UseProxy = true;
                }

                handler = sockets;
            }

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ScanHarness-SimpleScanner/1.0");
            return client;
        }

        /// <summary>Header the egress proxy adds to its own refusals, so a site's 403 isn't mistaken for one.</summary>
        public const string RefusedHeader = "x-egress-refused";

        // A plain-HTTP request refused by the proxy comes back as the proxy's 403/407, with the reason as the body.
        private static async Task<string?> RefusedAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ProxyAuthenticationRequired
            && response.Headers.Contains(RefusedHeader)
                ? (await response.Content.ReadAsStringAsync(cancellationToken)).Trim()
                : null;

        // An HTTPS tunnel the proxy refuses fails before any response from the site.
        private static bool IsProxyRefusal(Exception ex) =>
            ex is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError };

        private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxPageBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
    }
}
