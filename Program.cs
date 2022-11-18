using Akka.Actor;
using Akka.Hosting;
using Akka.Routing;
using AkkaStreamsHttp.Actors;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddAkka("StreamsSys", (configurationBuilder, provider) =>
{
    configurationBuilder.StartActors((system, registry) =>
    {
        var streamManager = system.ActorOf(Props.Create(() => new HttpStreamManager()), "stream-manager");
        registry.Register<HttpStreamManager>(streamManager);
    })
        .StartActors((system, registry) =>
        {
            var streamManager = registry.Get<HttpStreamManager>();
            var requestors =
                system.ActorOf(Props.Create(() => new RequestorActor(streamManager)).WithRouter(new RoundRobinPool(5)),
                    "requestors");
        });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapGet("/", context =>
{
    // extract the X-Client-ID header from the request if it exists
    var clientId = context.Request.Headers["ClientId"].FirstOrDefault() ?? "unknown";
    return context.Response.WriteAsync("Hello World! [ClientId: "+ clientId +"][RequestId: " +
                                       (context.Request.Headers.TryGetValue("RequestId", out var requestId)
                                           ? requestId.First()
                                           : "unknown") + "]");
});

app.Run();