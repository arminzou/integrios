using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Ingestion.ErrorHandling;
using Microsoft.AspNetCore.Http;

namespace Integrios.Ingestion.UnitTests;

public sealed class IngestionExceptionHandlerTests
{
    [Fact]
    public async Task EventAcceptanceException_MapsToStableErrorResponse()
    {
        var context = NewContext();
        var handler = new IngestionExceptionHandler();
        var exception = new EventAcceptanceException("invalid source");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        var body = await ResponseBodyAsync(context);
        body.GetProperty("error").GetString().ShouldBe(exception.Message);
    }

    [Fact]
    public async Task UnexpectedException_IsNotHandled()
    {
        var context = NewContext();
        var handler = new IngestionExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("unexpected"),
            CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task BadHttpRequestException_MapsToSanitized400Response()
    {
        var context = NewContext();
        var handler = new IngestionExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("binding details"),
            CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var body = await ResponseBodyAsync(context);
        body.GetProperty("error").GetString().ShouldBe("The request body is invalid.");
    }

    private static DefaultHttpContext NewContext() => new()
    {
        Response = { Body = new MemoryStream() }
    };

    private static async Task<JsonElement> ResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
    }
}
