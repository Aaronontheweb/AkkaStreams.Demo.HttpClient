# AkkaStreams.Demo.HttpClient

An ASP.NET Core + Akka.NET demo that pushes a stream of outbound HTTP requests
through an [Akka.Streams](https://getakka.net/articles/streams/introduction.html)
graph, applies a per-request **time-to-live (TTL) deadline**, retries timed-out
requests, and reports completion/failure back to the originating actor.

It's a worked example of the "request pipeline as a graph" pattern: instead of
each actor blocking on `HttpClient.SendAsync`, requests are materialized as data
in a stream, processed by fan-out worker streams, and their results routed back.

## What the pieces are

| File | Role |
|------|------|
| `Program.cs` | Boots Akka via `Akka.Hosting`, starts the `HttpStreamManager`, and a round-robin pool of 5 `RequestorActor`s. Also the HTTP endpoint that echoes a `ClientId` / `RequestId` back. |
| `Actors/RequestorActor.cs` | Generates `HttpRequestMessage`s on a timer and sends them to the stream manager. Receives `RequestTimedOut` / `RequestCompleted` / `RequestFailed` results. |
| `Actors/HttpStreamManager.cs` | Owns the whole stream graph. Wraps each request in a deadline, feeds it in through an actor-backed source, routes it through a `PartitionHub`, and materializes one HTTP-processor stream per client ID. |
| `StreamStages/RequestsWithDeadline.cs` | The `Deadline` struct, the result messages, and `RetriableRequestPipeline` — the TTL engine. |
| `StreamStages/ReusePrevious.cs` | A custom `GraphStage` that repeats the last upstream value when nothing new arrives. |
| `StreamStages/HttpClientStream.cs` | The `HttpClient` source (with token refresh) and `HttpHandlerFlow`, which does the actual send-with-retry. |

## How the TTL works

Every outbound request is stamped with a deadline the moment it enters the
graph. The deadline is a simple struct:

```csharp
public Deadline(TimeSpan timeout)
{
    Timeout       = timeout;
    DeadlineTime  = DateTime.UtcNow + timeout; // "born" timestamp
}

public bool IsOverdue => DeadlineTime < DateTime.UtcNow;
```

So each request carries an expiry computed at creation time
(`UtcNow + TimeSpan.FromSeconds(30)` in `HttpStreamManager`). Whether it's
still alive is decided by comparing that fixed timestamp against the current
time **when the tuple actually flows through the graph**.

The check lives in `RetriableRequestPipeline`, which uses `AlsoTo` to broadcast
each element to two branches at once:

```
                 ┌─ AlsoTo ── Where(IsOverdue) ──► notify requestor: RequestTimedOut
element ──► AlsoTo
                 └─ main ──── Where(!IsOverdue) ──► forward (request, requestor) downstream
```

* The `AlsoTo` branch filters for **overdue** requests and, for each, tells the
  originating `RequestorActor` "your request timed out."
* The main branch does the inverse — it keeps only requests whose deadline has
  **not** passed and lets them continue into the partition/HTTP stage.

Two things worth understanding about this TTL:

1. **It's evaluated lazily, on traversal, not on a wall-clock timer.** The cull
   happens when an element reaches the `AlsoTo` stage. Under healthy throughput
   every element is checked quickly, but a request only gets counted as
   "timed out" once its tuple actually gets pulled through that stage. It's a
   **best-effort floor**: it guarantees nothing that's stale ever goes out, but
   it doesn't fire the moment the clock passes the deadline.
2. **It's a separate mechanism from the per-attempt HTTP timeout.** Inside
   `HttpHandlerFlow` each `SendAsync` gets its own
   `CancellationTokenSource(timeout)` that starts at `initialTimeout` (3s) and
   **doubles on every retry** (`timeout += timeout`) up to `maxRetries` (3).
   That inner timeout measures a single network attempt. The outer TTL measures
   the whole request's lifetime in the graph. They compose, but they're not the
   same clock.

Because the retry backoff and the outer 30s deadline are independent, a request
could be retried several times (1 attempt + 2 retries ≈ 3+6+12 = 21s of budget)
and still complete before its 30s deadline, or it could blow the deadline if the
first attempt alone is slow — in which case the `AlsoTo` branch notifies the
requestor and the stale copy is dropped from the pipeline.

## How Akka.Streams is used

The demo strings together most of the common Akka.Streams shapes:

* **Actor-backed source** — `Source.ActorRef<(RequestsWithDeadline, IActorRef)>`
  with a `DropHead` overflow strategy and a 1000-element buffer. `HttpStreamManager`
  pushes requests into it via a `PreMaterialized` ref, which is what lets actors
  "talk to" the graph without blocking.
* **`AlsoTo` / `Where` branching** — used by `RetriableRequestPipeline` to split
  one stream into a TTL-notification side-channel and a forwarding main line.
* **`PartitionHub`** — a fan-out hub that routes each `(request, requestor)` to
  one of several subscribed downstream processors. A partitioning function
  (`requestor.Path.Name.GetHashCode() % i`) picks the lane so requests from the
  same actor go to the same processor.
* **`Zip`** to pair a request with an `HttpClient`, then **`SelectAsync(1)`** to
  run each HTTP send without interleaving.
* **`Source.Tick` + `RepeatLast`** — a tick every 30s triggers a token refresh,
  and the custom `RepeatLast` graph stage holds the most recent `HttpClient` in
  memory and replays it when no new one has been produced, so the downstream
  `Zip` always has a client available. (The `HttpHandlerFlow` callback in the
  same file is where the retry/double-backoff lives, chained off the zipped
  pair.)
* **`RestartSource.OnFailuresWithBackoff`** — wraps the client source so that if
  the token pipeline faults, the source restarts with exponential backoff
  (1s → 10s, factor 0.2).
* **`PreMaterialize`** — pulls the source's `(actorRef, source)` pair out up
  front so the manager holds the `IActorRef` and can push elements into an
  already-running graph.

Each client ID gets its own processor stream (`4` clients in the demo), so the
graph fans one request stream out to N parallel HTTP pipeline lanes.

## Running it

```bash
dotnet run
```

Start the app, then hit the root endpoint to see the echo:

```bash
curl -H "ClientId: client-0" -H "RequestId: abc123" http://localhost:5000
```

The `RequestorActor`s start firing `GET http://localhost:5000` after ~1s and
every 250ms; watch the console for the `Response:` / `Request timed out:` log
lines produced as results come back.

## Config knobs

| What | Where | Default |
|------|-------|---------|
| Request TTL deadline | `HttpStreamManager` `RetriableRequestPipeline(...)` | 30s |
| Inner per-attempt HTTP timeout | `HttpClientStream.CreateHttpProcessor(...)` `initialTimeout` | 3s (doubles per retry) |
| Max HTTP attempts | `CreateHttpProcessor(...)` `maxRetries` | 3 |
| Token-refresh tick | `HttpClientStream.CreateSourceInternal` `Source.Tick` | 30s |
| Token-fetch timeout | `tokenRefreshTimeout` arg | 1min |
| Requestor rate | `RequestorActor.Timers.StartPeriodicTimer` | initial 1s, then 250ms |
| Client lanes / processors | `HttpStreamManager` `ClientIDs` | 4 |

> Note: the `CreateSourceInternal` comment says "every 30 minutes" but the tick
> is actually 30 **seconds**. Everything in this table reflects what the code
> does.

## Requirements

The project targets `net8.0` with `Akka.Streams 1.5.71` + `Akka.Hosting 1.5.71`.
The SDK is pinned via `global.json`. Requires the .NET 8 SDK (or newer) to build.
