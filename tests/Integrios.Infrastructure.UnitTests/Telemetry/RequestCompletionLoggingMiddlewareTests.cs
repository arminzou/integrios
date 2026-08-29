using System.Diagnostics;
using Integrios.Infrastructure.Telemetry;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.UnitTests;

public sealed class RequestCompletionLoggingMiddlewareTests
{
    [Fact]
    public async Task RequestCompletion_LogsOnlyTheSafeContract()
    {
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var context = NewContext("/events/42?token=query-secret");
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Authorization = "Bearer header-secret";
        context.TraceIdentifier = "request-42";
        context.SetEndpoint(Route("/events/{event_id}"));

        using var activity = new Activity("request").Start();
        var middleware = new RequestCompletionLoggingMiddleware(
            next: current =>
            {
                current.Response.StatusCode = StatusCodes.Status202Accepted;
                return Task.CompletedTask;
            },
            loggerFactory.CreateLogger<RequestCompletionLoggingMiddleware>());

        await middleware.InvokeAsync(context);

        CapturedLogRecord record = logs.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Information);
        record.EventId.Name.ShouldBe("HttpRequestCompleted");
        record.Exception.ShouldBeNull();
        Dictionary<string, object?> properties = Properties(record.State);
        properties.Where(pair => pair.Key != "duration_ms").ToDictionary().ShouldBe(new Dictionary<string, object?>
        {
            ["method"] = "POST",
            ["route_template"] = "/events/{event_id}",
            ["status"] = StatusCodes.Status202Accepted,
            ["request_id"] = "request-42"
        }, ignoreOrder: true);
        properties["duration_ms"].ShouldBeOfType<double>().ShouldBeGreaterThanOrEqualTo(0);
        record.Message.ShouldNotContain("query-secret");
        record.Message.ShouldNotContain("header-secret");
        Activity.Current!.TraceId.ShouldNotBe(default);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/metrics")]
    [InlineData("/_framework/app.js")]
    [InlineData("/_content/library.js")]
    [InlineData("/assets/dashboard.js")]
    [InlineData("/favicon.ico")]
    public async Task OperationalRequest_DoesNotLogCompletion(string path)
    {
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var middleware = new RequestCompletionLoggingMiddleware(
            next: _ => Task.CompletedTask,
            loggerFactory.CreateLogger<RequestCompletionLoggingMiddleware>());

        await middleware.InvokeAsync(NewContext(path));

        logs.Records.ShouldBeEmpty();
    }

    private static DefaultHttpContext NewContext(string target) => new()
    {
        Request = { Path = target.Split('?', 2)[0], QueryString = new QueryString(target.Contains('?') ? target[target.IndexOf('?')..] : string.Empty) }
    };

    private static RouteEndpoint Route(string pattern) => new(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(pattern),
        order: 0,
        EndpointMetadataCollection.Empty,
        displayName: pattern);

    private static Dictionary<string, object?> Properties(object state) =>
        ((IEnumerable<KeyValuePair<string, object?>>)state)
            .Where(pair => pair.Key != "{OriginalFormat}")
            .ToDictionary(pair => pair.Key, pair => pair.Value);
}
