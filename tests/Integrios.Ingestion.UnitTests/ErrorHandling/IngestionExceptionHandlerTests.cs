using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Ingestion.ErrorHandling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingestion.UnitTests;

public sealed class IngestionExceptionHandlerTests
{
    public static TheoryData<Exception, int> ExpectedExceptions => new()
    {
        { new EventAcceptanceException("invalid source"), StatusCodes.Status422UnprocessableEntity },
        { new SourceEndpointNotFoundException("source not found"), StatusCodes.Status404NotFound },
        { new SourceVerificationException("verification failed"), StatusCodes.Status401Unauthorized },
        { new WebhookPayloadException("invalid payload"), StatusCodes.Status400BadRequest },
        { new BadHttpRequestException("binding details"), StatusCodes.Status400BadRequest }
    };

    [Theory]
    [MemberData(nameof(ExpectedExceptions))]
    public async Task ExpectedException_MapsToProblemDetails(Exception exception, int expectedStatusCode)
    {
        using ServiceProvider services = NewServices();
        var context = NewContext(services);
        var handler = new IngestionExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(expectedStatusCode);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        var body = await ResponseBodyAsync(context);
        body.GetProperty("status").GetInt32().ShouldBe(expectedStatusCode);
        body.GetProperty("detail").GetString().ShouldBe(
            exception is BadHttpRequestException ? "The request is invalid." : exception.Message);
        body.TryGetProperty("error", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UnexpectedException_IsNotHandled()
    {
        using ServiceProvider services = NewServices();
        var context = NewContext(services);
        var handler = new IngestionExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("unexpected"),
            CancellationToken.None);

        handled.ShouldBeFalse();
    }

    private static ServiceProvider NewServices() => new ServiceCollection()
        .AddOptions()
        .AddProblemDetails()
        .BuildServiceProvider();

    private static DefaultHttpContext NewContext(IServiceProvider services) => new()
    {
        RequestServices = services,
        Response = { Body = new MemoryStream() }
    };

    private static async Task<JsonElement> ResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
    }
}
