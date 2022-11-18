using Akka.Actor;
using Akka.Event;

namespace AkkaStreamsHttp.Actors;

public sealed class RequestorActor : ReceiveActor, IWithTimers
{
    public sealed class Request
    {
        public static readonly Request Instance = new();
        private Request(){}
    }

    private readonly ILoggingAdapter _log = Context.GetLogger();
    
    // generate HTTP Request messages for https://localhost:5000 and send them to an IActorRef we receive in the constructor
    public RequestorActor(IActorRef httpActor)
    {
        Receive<Request>(request =>
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000");
            httpRequest.Headers.Add("RequestId", Guid.NewGuid().ToString());
            httpActor.Tell(httpRequest);
        });
        
        // handle RequestTimedOut
        Receive<RequestTimedOut>(requestTimedOut =>
        {
            _log.Info("Request timed out: [RequestId: {0}]", requestTimedOut.Request.Headers.TryGetValues("RequestId", out var requestId) ? requestId.First() : "unknown");
        });
        
        // handle RequestCompleted and print the response plus RequestId header
        ReceiveAsync<RequestCompleted>(async requestCompleted =>
        {
            _log.Info("Response: {0}", await requestCompleted.ResponseMessage.Content.ReadAsStringAsync());
        });
        
        // handle RequestFailed
        Receive<RequestFailed>(requestFailed =>
        {
            _log.Info("Request failed");
        });
        
        Timers!.StartPeriodicTimer("request", Request.Instance, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.25));
    }

    public ITimerScheduler Timers { get; set; }
}