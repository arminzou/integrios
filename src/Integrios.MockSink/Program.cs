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
var sinkReceipts = new ConcurrentDictionary<string, ConcurrentQueue<SinkReceipt>>(StringComparer.OrdinalIgnoreCase);
long nextReceiptId = 0;

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Delivery endpoint — Worker posts events here.
app.MapPost("/sink/{name}", async (string name, HttpRequest request, ILogger<Program> logger) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var mode = sinkModes.GetValueOrDefault(name, SinkMode.Default);
    var headers = request.Headers.ToDictionary(
        header => header.Key,
        header => header.Value.Select(value => value ?? string.Empty).ToArray(),
        StringComparer.OrdinalIgnoreCase);
    var receipt = new SinkReceipt(
        Interlocked.Increment(ref nextReceiptId),
        request.Method,
        request.Path,
        body,
        headers);

    sinkReceipts.GetOrAdd(name, _ => new ConcurrentQueue<SinkReceipt>()).Enqueue(receipt);

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

// A controlled redirect proves that delivery treats the configured URL as the complete
// Operator-selected boundary. The Worker must record the redirect response and never send a
// second request (or its credentials) to the Location target.
app.MapPost("/redirect/{name}", (string name) =>
    Results.Redirect($"/sink/{name}", permanent: false, preserveMethod: true));

// Receipt queries expose request metadata and header names, but never header values.
app.MapGet("/receipts/{name}", (string name) =>
{
    SinkReceipt[] receipts = sinkReceipts.TryGetValue(name, out var queue)
        ? queue.ToArray()
        : [];

    return Results.Ok(new
    {
        sink = name,
        count = receipts.Length,
        receipts = receipts.Select(receipt => new
        {
            receipt.Id,
            receipt.Method,
            path = receipt.Path.Value,
            receipt.Body,
            headerNames = receipt.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase),
        }),
    });
});

// Assert exact header values without returning or logging those values as test evidence.
app.MapPost("/receipts/{name}/assert-headers", (string name, HeaderAssertionRequest assertion) =>
{
    SinkReceipt[] receipts = sinkReceipts.TryGetValue(name, out var queue)
        ? queue.ToArray()
        : [];
    string[] assertedHeaders = assertion.Headers.Keys
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    bool matched = receipts.Any(receipt => assertion.Headers.All(
        header => HeaderMatches(receipt, header.Key, header.Value)));

    if (!matched)
    {
        return Results.Conflict(new
        {
            matched = false,
            receiptCount = receipts.Length,
            assertedHeaders,
        });
    }

    return Results.Ok(new { matched = true, receiptCount = receipts.Length });
});

app.MapDelete("/receipts/{name}", (string name) =>
{
    sinkReceipts.TryRemove(name, out _);
    return Results.Ok(new { sink = name, count = 0 });
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

static bool HeaderMatches(SinkReceipt receipt, string headerName, string expectedValue) =>
    receipt.Headers.TryGetValue(headerName, out string[]? actualValues)
    && actualValues.Contains(expectedValue, StringComparer.Ordinal);

record SinkMode(string Behavior, int DelayMs)
{
    public static SinkMode Default => new("succeed", 0);
}

record SinkModeRequest(string? Mode, int? DelayMs);

record SinkReceipt(
    long Id,
    string Method,
    PathString Path,
    string Body,
    IReadOnlyDictionary<string, string[]> Headers);

record HeaderAssertionRequest(IReadOnlyDictionary<string, string> Headers);
