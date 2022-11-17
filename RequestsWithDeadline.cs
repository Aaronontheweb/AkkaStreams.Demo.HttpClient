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
}