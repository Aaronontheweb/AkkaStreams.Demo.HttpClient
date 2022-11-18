using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Util;

namespace AkkaStreamsHttp.Actors;

public sealed class HttpStreamManager : ReceiveActor
{
    private IActorRef _source;
    
    private static readonly string[] ClientIDs = Enumerable.Range(0, 4).Select(c => $"client-{c}").ToArray(); 
    private static readonly Func<Task<string>> TokenProvider = () => Task.FromResult(Guid.NewGuid().ToString());
    
    public HttpStreamManager()
    {
        Receive<HttpRequestMessage>(start =>
        {
            
        });
    }

    protected override void PreStart()
    {
        var (actorRef, source) = Source.ActorRef<(HttpRequestMessage req, IActorRef requestor)>(1000, OverflowStrategy.Backpressure)
            .RetriableRequestPipeline(TimeSpan.FromSeconds(30)) // 30 second deadline to process each HTTP request
            .PreMaterialize(Context.Materializer());
        _source = actorRef;
        
        

        int PartitioningFunction(int i, (HttpRequestMessage req, IActorRef requestor) tuple) => tuple.requestor.Path.Name.GetHashCode() % i;

        // create hub (we'll attach HttpClients to this after it starts)
        
        var hub = PartitionHub.Sink<(HttpRequestMessage req, IActorRef requestor)>(PartitioningFunction, 2, 1024);
        
        // begin running top part of graph (HTTP request processing pipeline)
        var hubSource = source.ToMaterialized(hub, Keep.Right).Run(Context.Materializer());

        var httpProcessors =
            ClientIDs.Select(id => HttpClientStream.CreateSource(id, TokenProvider, TimeSpan.FromMinutes(1)))
                .Select(c => HttpClientStream.CreateHttpProcessor(c, hubSource, TimeSpan.FromSeconds(3)))
                .Select(s => s.RunForeach(tuple =>
                {
                    
                }));
    }
}