using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;

namespace AkkaStreamsHttp;

// create static class and extension method to add RepeatLast flow to a source
public static class RepeatLastFlow
{
    public static Source<T, TMat> RepeatLast<T, TMat>(this Source<T, TMat> source)
    {
        return source.Via(new RepeatLast<T>());
    }
    
    // extension method to add RepeatLast flow to a flow
    public static Flow<TIn, TOut, TMat> RepeatLast<TIn, TOut, TMat>(this Flow<TIn, TOut, TMat> flow)
    {
        return flow.Via(new RepeatLast<TOut>());
    }
}

// create an Akka.NET Streams Flow stage that repeats the previous value unless a new value is pushed into it from upstream
public sealed class RepeatLast<T> : GraphStage<FlowShape<T, T>>
{
    private readonly Inlet<T> _in = new Inlet<T>("RepeatLast.in");
    private readonly Outlet<T> _out = new Outlet<T>("RepeatLast.out");

    public override FlowShape<T, T> Shape => new FlowShape<T, T>(_in, _out);

    protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

    private sealed class Logic : GraphStageLogic
    {
        private readonly RepeatLast<T> _stage;
        private T _last;

        public Logic(RepeatLast<T> stage) : base(stage.Shape)
        {
            _stage = stage;
            SetHandler(stage._in, onPush: () =>
            {
                _last = Grab(stage._in);
                Push(stage._out, _last);
            }, onUpstreamFinish: () => Complete(stage._out));

            SetHandler(stage._out, onPull: () =>
            {
                if (_last != null)
                    Push(stage._out, _last);
            });
        }
    }
}
