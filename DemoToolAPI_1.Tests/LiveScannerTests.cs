using System.Net;
using System.Text;
using DemoToolAPI_1.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace DemoToolAPI_1.Tests
{
    /// <summary>The scanner's live mode, with the network replaced by a fake that behaves like the egress proxy.</summary>
    public class LiveScannerTests
    {
        private const string Page = """
            <html><body>
              <a href="/about">About <b>us</b></a>
              <a href='https://shop.acme.test/cart'>Cart</a>
              <a href="https://evil.example/x">Elsewhere</a>
              <a href="https://cdn.other.example/y">CDN</a>
              <a href="/about">About again</a>
              <a href="mailto:someone@acme.test">Mail</a>
              <a href="#top">Top</a>
              <a href="https://shop.acme.test/"><img src="logo.png"></a>
            </body></html>
            """;

        private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                return Task.FromResult(respond(request));
            }
        }

        private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

        private static LiveScanner Scanner(FakeNetwork network, int followLinks = 5) =>
            new(Options.Create(new ScannerOptions { LiveFetch = true, FollowLinks = followLinks }), () => network);

        [Fact]
        public void Links_are_absolute_http_and_distinct_with_readable_text()
        {
            var links = LiveScanner.ExtractLinks(Page, new Uri("https://shop.acme.test/home"));

            Assert.Equal(new[] { "https://shop.acme.test/about", "https://shop.acme.test/cart", "https://evil.example/x", "https://cdn.other.example/y", "https://shop.acme.test/" },
                links.Select(l => l.Url));
            Assert.Equal("About us", links[0].LinkText);
            Assert.Equal("https://shop.acme.test/", links[4].LinkText); // no text: the URL stands in
        }

        [Fact]
        public async Task One_link_per_host_is_followed_and_proxy_refusals_are_reported()
        {
            var network = new FakeNetwork(request => request.RequestUri!.Host switch
            {
                "shop.acme.test" => Html(Page),
                // A plain-HTTP refusal: the proxy's own 403, marked with its header, the reason as the body.
                "evil.example" => new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Refused: nothing allows simple_scanner to reach evil.example."),
                    Headers = { { LiveScanner.RefusedHeader, "1" } },
                },
                // An HTTPS tunnel the proxy refuses fails before the site answers.
                _ => throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "The proxy tunnel request failed with status code '403'."),
            });

            var result = await Scanner(network).ScanAsync("https://shop.acme.test/home", "token", CancellationToken.None);

            Assert.Equal("success", result.Status);
            Assert.Contains("Followed 3; the egress proxy refused 2.", result.Message);
            Assert.Equal("reachable (200)", result.Links[0].Note);
            Assert.Null(result.Links[1].Note); // same host as the first: not followed
            Assert.Equal("refused by the egress proxy: Refused: nothing allows simple_scanner to reach evil.example.", result.Links[2].Note);
            Assert.Equal("refused by the egress proxy", result.Links[3].Note);
            Assert.Equal(4, network.Requests.Count);
        }

        [Fact]
        public async Task A_refused_page_is_reported_as_blocked()
        {
            var network = new FakeNetwork(_ => throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "403"));

            var result = await Scanner(network).ScanAsync("evil.example", "token", CancellationToken.None);

            Assert.Equal("blocked", result.Status);
            Assert.Equal("The egress proxy refused evil.example.", result.Message);
        }

        [Fact]
        public async Task A_site_403_is_not_mistaken_for_the_proxy()
        {
            var network = new FakeNetwork(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("nope") });

            var result = await Scanner(network).ScanAsync("https://shop.acme.test", "token", CancellationToken.None);

            Assert.Equal("failure", result.Status);
            Assert.Contains("403", result.Message);
        }

        [Theory]
        [InlineData("ftp://shop.acme.test")]
        [InlineData("https://")]
        public async Task Non_http_urls_are_refused_without_a_request(string url)
        {
            var network = new FakeNetwork(_ => Html(""));

            var result = await Scanner(network).ScanAsync(url, null, CancellationToken.None);

            Assert.Equal("error", result.Status);
            Assert.Empty(network.Requests);
        }
    }
}
