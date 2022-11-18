using System.Net.Http.Headers;
using Akka;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Util;
using static AkkaStreamsHttp.RepeatLastFlow;

namespace AkkaStreamsHttp;

public static class HttpClientStream
{
    // create method that produces an HttpClient with authentication token and unique client identifier
    public static HttpClient CreateClient(string token, string clientId)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("ClientId", clientId);
        return client;
    }

    // create Akka.Streams source that will produce an HttpClient with the given clientId and will automatically refresh the bearer token every 30 minutes, producing a new or updated client
    private static Source<HttpClient, ICancelable> CreateSourceInternal(string clientId,
        Func<Task<string>> tokenProvider, TimeSpan tokenRefreshTimeout)
    {
        var source = Source.Tick(TimeSpan.Zero, TimeSpan.FromSeconds(30), clientId)
            .SelectAsync(1, async c => CreateClient(c, (await tokenProvider().WaitAsync(tokenRefreshTimeout))))
            .RepeatLast();
        return source;
    }

    // create Akka.Streams RestartSource that calls CreateSource and restarts the stream if it fails
    public static Source<HttpClient, NotUsed> CreateSource(string clientId, Func<Task<string>> tokenProvider,
        TimeSpan tokenRefreshTimeout)
    {
        var restartSource = RestartSource.OnFailuresWithBackoff(
            () => CreateSourceInternal(clientId, tokenProvider, tokenRefreshTimeout),
            RestartSettings.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 0.2));
        return restartSource;
    }

    public static Source<(Option<HttpResponseMessage> response, IActorRef requestor), NotUsed> CreateHttpProcessor(
        Source<HttpClient, NotUsed> httpClientSource, Source<(HttpRequestMessage req, IActorRef requestor), NotUsed> requestSource, TimeSpan initialTimeout, int maxRetries = 3)
    {
        var flow = httpClientSource.Zip(requestSource)
            .SelectAsync(1, async c => (await HttpHandlerFlow(c.Item1, c.Item2.req, initialTimeout, maxRetries), c.Item2.requestor));
        return flow;
    }

    public static async Task<Option<HttpResponseMessage>> HttpHandlerFlow(HttpClient client, HttpRequestMessage request,
        TimeSpan initialTimeout, int maxRetries)
    {
        var timeout = initialTimeout;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                return await client.SendAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                var e = ex;
                // no exactly "exponential" backoff, but good enough for this example
                timeout += timeout;
            }
        }

        return Option<HttpResponseMessage>.None;
    }
}