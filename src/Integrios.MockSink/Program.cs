using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Per-sink behavior configuration. Default: succeed.
// PUT /control/{name}  body: {"mode":"succeed"|"fail"|"slow","delayMs":2000}
var sinkModes = new ConcurrentDictionary<string, SinkMode>(StringComparer.OrdinalIgnoreCase);

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Delivery endpoint — Worker posts events here.
app.MapPost("/sink/{name}", async (string name, HttpRequest request, ILogger<Program> logger) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var mode = sinkModes.GetValueOrDefault(name, SinkMode.Default);

    logger.LogInformation("[MockSink] {Name} received event (mode={Mode}): {Body}", name, mode.Behavior, body);

    if (mode.Behavior == "slow")
        await Task.Delay(mode.DelayMs);

    if (mode.Behavior == "fail")
    {
        logger.LogWarning("[MockSink] {Name} returning 500 (configured to fail)", name);
        return Results.StatusCode(500);
    }

    return Results.Ok(new { sink = name, received = true });
});

// Control endpoint — sets delivery behavior for a named sink.
app.MapPut("/control/{name}", (string name, SinkModeRequest req) =>
{
    var mode = new SinkMode(req.Mode ?? "succeed", req.DelayMs ?? 2000);
    sinkModes[name] = mode;
    return Results.Ok(new { sink = name, mode = mode.Behavior, delayMs = mode.DelayMs });
});

// Reset a sink back to default success behavior.
app.MapDelete("/control/{name}", (string name) =>
{
    sinkModes.TryRemove(name, out _);
    return Results.Ok(new { sink = name, mode = "succeed" });
});

app.Run();

record SinkMode(string Behavior, int DelayMs)
{
    public static SinkMode Default => new("succeed", 0);
}

record SinkModeRequest(string? Mode, int? DelayMs);
