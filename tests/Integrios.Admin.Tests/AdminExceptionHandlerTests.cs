using System.Text.Json;
using Integrios.Admin.ErrorHandling;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Connections;
using Integrios.Application.Subscriptions;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Http;

namespace Integrios.Admin.Tests;

public sealed class AdminExceptionHandlerTests
{
    public static TheoryData<Exception, int> ExpectedExceptions => new()
    {
        { new DuplicateResourceException("duplicate"), StatusCodes.Status409Conflict },
        { new ConnectionAuthoringConflictException(), StatusCodes.Status409Conflict },
        { new TenantRequestValidationException("invalid tenant"), StatusCodes.Status422UnprocessableEntity },
        { new ConnectionRequestValidationException("invalid connection"), StatusCodes.Status422UnprocessableEntity },
        { new TopicRequestValidationException("invalid topic"), StatusCodes.Status422UnprocessableEntity },
        { new SubscriptionRequestValidationException("invalid subscription"), StatusCodes.Status422UnprocessableEntity },
        { new BadHttpRequestException("binding details"), StatusCodes.Status400BadRequest }
    };

    [Theory]
    [MemberData(nameof(ExpectedExceptions))]
    public async Task ExpectedApplicationException_MapsToStableErrorResponse(
        Exception exception,
        int expectedStatusCode)
    {
        var context = NewContext();
        var handler = new AdminExceptionHandler();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        var body = await ResponseBodyAsync(context);
        var expectedMessage = exception is BadHttpRequestException
            ? "The request body is invalid."
            : exception.Message;
        Assert.Equal(expectedMessage, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnexpectedException_IsNotHandled()
    {
        var context = NewContext();
        var handler = new AdminExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("unexpected"),
            CancellationToken.None);

        Assert.False(handled);
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
