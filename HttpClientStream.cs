using System.Net.Http.Headers;
using Akka;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace AkkaStreamsHttp;

public static class HttpClientStream
{
    // create method that produces an HttpClient with authentication token and unique client identifier
    public static HttpClient CreateClient(string token, string clientId)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Client-ID", clientId);
        return client;
    }
    
    // create Akka.Streams source that will produce an HttpClient with the given clientId and will automatically refresh the bearer token every 30 minutes, producing a new or updated client
    private static Source<HttpClient, ICancelable> CreateSourceInternal(string clientId, Func<Task<string>> tokenProvider, TimeSpan tokenRefreshTimeout)
    {
        var source = Source.Tick(TimeSpan.Zero, TimeSpan.FromSeconds(30), clientId)
            .SelectAsync(1, async c => CreateClient(c, (await tokenProvider().WaitAsync(tokenRefreshTimeout))));
        return source;
    }
    
    // create Akka.Streams RestartSource that calls CreateSource and restarts the stream if it fails
    public static Source<HttpClient, NotUsed> CreateSource(string clientId, Func<Task<string>> tokenProvider, TimeSpan tokenRefreshTimeout)
    {
        var restartSource = RestartSource.OnFailuresWithBackoff(
             () => CreateSourceInternal(clientId, tokenProvider, tokenRefreshTimeout), 
             RestartSettings.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 0.2));
        return restartSource;
    }

    public static Flow<HttpRequestMessage, HttpResponseMessage, NotUsed> CreateHttpProcessor(
        Source<HttpClient, NotUsed> httpClientSource, Source<HttpRequestMessage, NotUsed> requestSource)
    {
        
    }
}
