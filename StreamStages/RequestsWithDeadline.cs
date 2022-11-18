using Akka;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace AkkaStreamsHttp;

// Deadline struct in C# computed from the current time and the timeout
// value. The deadline is used to determine if a request has timed out.
public readonly struct Deadline
{
    public Deadline(TimeSpan timeout)
    {
        Timeout = timeout;
        DeadlineTime = DateTime.UtcNow + timeout;
    }

    public TimeSpan Timeout { get; }
    public DateTime DeadlineTime { get; }

    public bool IsOverdue => DeadlineTime < DateTime.UtcNow;
}

public sealed class RequestTimedOut
{
    public RequestTimedOut(HttpRequestMessage request)
    {
        Request = request;
    }

    public HttpRequestMessage Request { get; }
}

public sealed class RequestCompleted
{
    public RequestCompleted(HttpResponseMessage responseMessage)
    {
        ResponseMessage = responseMessage;
    }

    public HttpResponseMessage ResponseMessage { get; }
}


public readonly struct RequestsWithDeadline
{
    public RequestsWithDeadline(HttpRequestMessage request, Deadline deadline)
    {
        Request = request;
        Deadline = deadline;
    }

    public HttpRequestMessage Request { get; }
    
    public Deadline Deadline { get; }
    
    // method to refresh deadline with new timeout
    public RequestsWithDeadline WithNewTimeout(TimeSpan timeout) => new RequestsWithDeadline(Request, new Deadline(timeout));
}

// extenstion method to create a new RequestsWithDeadline from a HttpRequestMessage
public static class HttpRequestMessageExtensions
{
    public static RequestsWithDeadline WithDeadline(this HttpRequestMessage request, TimeSpan timeout) => new RequestsWithDeadline(request, new Deadline(timeout));
    
    // method that returns an Akka.Streams graph that will automatically retry a request if it times out
    public static Source<(HttpRequestMessage req, IActorRef requestor), TMat> RetriableRequestPipeline<TMat>(this Source<(HttpRequestMessage req, IActorRef requestor), TMat> source, TimeSpan timeout, int maxRetries)
    {
        var src = source
            .Select(request => (request.req.WithDeadline(timeout), request.requestor))
            .Buffer(10 * 1024, OverflowStrategy.Backpressure)
            .AlsoTo(Flow.Create<(RequestsWithDeadline req, IActorRef requestor), TMat>()
                .Where(c => c.req.Deadline.IsOverdue)
                .To(Sink.ForEach<(RequestsWithDeadline req, IActorRef requestor)>(tuple =>
                {
                    tuple.requestor.Tell(new RequestTimedOut(tuple.req.Request));
                })))
            .Where(c => c.Item1.Deadline.IsOverdue == false)
            .Select(c => (c.Item1.Request, c.requestor));

        return src;
    }
}