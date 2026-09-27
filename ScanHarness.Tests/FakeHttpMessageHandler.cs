using System.Net;
using System.Text;

namespace ScanHarness.Tests
{
    /// <summary>
    /// Test double for <see cref="HttpMessageHandler"/>: records each request (with its body)
    /// and answers with a canned response or exception.
    /// </summary>
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        /// <summary>Requests received, in order, with their bodies read into memory.</summary>
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        /// <summary>A handler that always returns <paramref name="json"/> with the given status.</summary>
        public static FakeHttpMessageHandler Returning(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        /// <summary>A handler that always throws <paramref name="exception"/>.</summary>
        public static FakeHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

        /// <summary>Creates an <see cref="HttpClient"/> over this handler with an OpenAI-style base URL.</summary>
        public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://llm.test/v1/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return _respond(request);
        }
    }
}
