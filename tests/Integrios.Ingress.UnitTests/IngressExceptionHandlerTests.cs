using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Ingress.ErrorHandling;
using Microsoft.AspNetCore.Http;

namespace Integrios.Ingress.UnitTests;

public sealed class IngressExceptionHandlerTests
{
    [Fact]
    public async Task EventAcceptanceException_MapsToStableErrorResponse()
    {
        var context = NewContext();
        var handler = new IngressExceptionHandler();
        var exception = new EventAcceptanceException("invalid source");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        var body = await ResponseBodyAsync(context);
        Assert.Equal(exception.Message, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnexpectedException_IsNotHandled()
    {
        var context = NewContext();
        var handler = new IngressExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("unexpected"),
            CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task BadHttpRequestException_MapsToSanitized400Response()
    {
        var context = NewContext();
        var handler = new IngressExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("binding details"),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ResponseBodyAsync(context);
        Assert.Equal("The request body is invalid.", body.GetProperty("error").GetString());
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
