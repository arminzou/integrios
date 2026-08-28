using System.Text.Json;
using Integrios.Admin.ErrorHandling;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.Tenants;
using Integrios.Application.Authoring.Topics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Admin.UnitTests;

public sealed class AdminExceptionHandlerTests
{
    public static TheoryData<Exception, int> ExpectedExceptions => new()
    {
        { new DuplicateResourceException("duplicate"), StatusCodes.Status409Conflict },
        { new ConnectionAuthoringConflictException(), StatusCodes.Status409Conflict },
        { new TenantValidationException("invalid tenant"), StatusCodes.Status422UnprocessableEntity },
        { new ConnectionValidationException("invalid connection"), StatusCodes.Status422UnprocessableEntity },
        { new TopicValidationException("invalid topic"), StatusCodes.Status422UnprocessableEntity },
        { new SubscriptionValidationException("invalid subscription"), StatusCodes.Status422UnprocessableEntity },
        { new BadHttpRequestException("binding details"), StatusCodes.Status400BadRequest }
    };

    [Theory]
    [MemberData(nameof(ExpectedExceptions))]
    public async Task ExpectedApplicationException_MapsToProblemDetails(
        Exception exception,
        int expectedStatusCode)
    {
        using ServiceProvider services = NewServices();
        var context = NewContext(services);
        var handler = new AdminExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(expectedStatusCode);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        var body = await ResponseBodyAsync(context);
        var expectedMessage = exception is BadHttpRequestException
            ? "The request is invalid."
            : exception.Message;
        body.GetProperty("status").GetInt32().ShouldBe(expectedStatusCode);
        body.TryGetProperty("error", out _).ShouldBeFalse();

        if (expectedStatusCode == StatusCodes.Status422UnprocessableEntity)
            body.GetProperty("errors").GetProperty("")[0].GetString().ShouldBe(expectedMessage);
        else
            body.GetProperty("detail").GetString().ShouldBe(expectedMessage);
    }

    [Fact]
    public async Task ValidationException_PreservesApplicationField()
    {
        using ServiceProvider services = NewServices();
        var context = NewContext(services);
        var handler = new AdminExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(
            context,
            new TenantValidationException("Name is required.", "name"),
            CancellationToken.None);

        handled.ShouldBeTrue();
        var body = await ResponseBodyAsync(context);
        body.GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("Name is required.");
    }

    [Fact]
    public async Task UnexpectedException_IsNotHandled()
    {
        using ServiceProvider services = NewServices();
        var context = NewContext(services);
        var handler = new AdminExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

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
