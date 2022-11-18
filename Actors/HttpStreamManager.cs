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
            var addDeadline = start.WithDeadline(TimeSpan.FromSeconds(30));
            // drive the stream forward
            _source.Tell((addDeadline, Sender));
        });
    }

    protected override void PreStart()
    {
        var (actorRef, source) = Source.ActorRef<(RequestsWithDeadline req, IActorRef requestor)>(1000, OverflowStrategy.DropHead)
            .RetriableRequestPipeline(TimeSpan.FromSeconds(30)) // 30 second deadline to process each HTTP request
            .PreMaterialize(Context.Materializer());
        _source = actorRef;
        
        

        int PartitioningFunction(int i, (HttpRequestMessage req, IActorRef requestor) tuple)
        {
            return Math.Abs(tuple.requestor.Path.Name.GetHashCode() % i);
        }

        // create hub (we'll attach HttpClients to this after it starts)
        
        var hub = PartitionHub.Sink<(HttpRequestMessage req, IActorRef requestor)>(PartitioningFunction, 2, 1024);
        
        // begin running top part of graph (HTTP request processing pipeline)
        var hubSource = source.ToMaterialized(hub, Keep.Right).Run(Context.Materializer());

        var httpProcessors =
            ClientIDs.Select(id => HttpClientStream.CreateSource(id, TokenProvider, TimeSpan.FromMinutes(1)))
                .Select(c => HttpClientStream.CreateHttpProcessor(c, hubSource, TimeSpan.FromSeconds(3)));

        foreach (var proc in httpProcessors)
        {
            proc.RunForeach(tuple =>
            {
                var (response, requestor) = tuple;
                if (response.HasValue)
                    requestor.Tell(new RequestCompleted(response.Value));
                else
                    requestor.Tell(new RequestFailed());
            }, Context.Materializer());
        }
    }
}