using System.Security.Claims;
using System.Text.Json;
using Integrios.Application.Subscriber;
using Integrios.Subscriber.Auth;
using MediatR;

namespace Integrios.Subscriber.Endpoints;

public static class DeliveryEndpoints
{
    private const int MaxClaim = 32;
    private const int MaxWaitSeconds = 20;
    // The same cap push applies to a destination's Retry-After header.
    private const int MaxRetryAfterSeconds = 900;
    private const int MaxErrorLength = 2000;

    public static void MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/delivery/v1").RequireAuthorization();
        group.MapPost("/claims", Claim).WithName(nameof(Claim))
            .Produces<ClaimResponse>().Produces(StatusCodes.Status204NoContent);

        RouteGroupBuilder attempt = group.MapGroup("/deliveries/{deliveryId:guid}/attempts/{attemptId:guid}");
        attempt.MapPost("/renew", Renew).WithName(nameof(Renew)).Produces<RenewResponse>();
        attempt.MapPost("/ack", Ack).WithName(nameof(Ack)).Produces(StatusCodes.Status204NoContent);
        attempt.MapPost("/nack", Nack).WithName(nameof(Nack)).Produces(StatusCodes.Status204NoContent);
        attempt.MapPost("/fail", Fail).WithName(nameof(Fail)).Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> Claim(
        ClaimRequest request,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Max is not (>= 1 and <= MaxClaim))
            errors["max"] = [$"max must be an integer from 1 to {MaxClaim}."];
        if (request.WaitSeconds is not (>= 0 and <= MaxWaitSeconds))
            errors["wait_seconds"] = [$"wait_seconds must be an integer from 0 to {MaxWaitSeconds}."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        IReadOnlyList<PullDelivery> claimed = await mediator.Send(
            new ClaimPullDeliveriesCommand(
                DestinationId(user),
                request.Max!.Value,
                TimeSpan.FromSeconds(request.WaitSeconds!.Value)),
            cancellationToken);
        return claimed.Count == 0
            ? Results.NoContent()
            : Results.Ok(new ClaimResponse([.. claimed.Select(ClaimedDelivery.From)]));
    }

    private static async Task<IResult> Renew(
        Guid deliveryId,
        Guid attemptId,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        PullLeaseRenewalResult result = await mediator.Send(
            new RenewPullLeaseCommand(new PullAttemptOwner(DestinationId(user), deliveryId, attemptId)),
            cancellationToken);
        return result.Status switch
        {
            PullLeaseRenewalStatus.Renewed => Results.Ok(new RenewResponse(result.LeaseExpiresAt!.Value)),
            PullLeaseRenewalStatus.MaxAttemptDurationReached => Conflict(
                "max_attempt_duration",
                "The attempt reached its maximum duration and cannot be renewed. Its lease lapses, and a later claim retries the delivery."),
            _ => OwnershipLost(),
        };
    }

    private static Task<IResult> Ack(
        Guid deliveryId,
        Guid attemptId,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        Settle(user, deliveryId, attemptId, PullSettlement.Ack(), mediator, cancellationToken);

    private static async Task<IResult> Nack(
        Guid deliveryId,
        Guid attemptId,
        NackRequest? request,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request?.RetryAfterSeconds is not (null or (>= 0 and <= MaxRetryAfterSeconds)))
            errors["retry_after_seconds"] = [$"retry_after_seconds must be an integer from 0 to {MaxRetryAfterSeconds}."];
        if (request?.Error?.Length > MaxErrorLength)
            errors["error"] = [$"error must be at most {MaxErrorLength} characters."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        TimeSpan? retryAfter = request?.RetryAfterSeconds is { } delay ? TimeSpan.FromSeconds(delay) : null;
        return await Settle(
            user, deliveryId, attemptId, PullSettlement.Nack(request?.Error, retryAfter), mediator, cancellationToken);
    }

    private static async Task<IResult> Fail(
        Guid deliveryId,
        Guid attemptId,
        FailRequest request,
        ClaimsPrincipal user,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Error) || request.Error.Length > MaxErrorLength)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["error"] = [$"error is required and must be at most {MaxErrorLength} characters."]
            });

        return await Settle(
            user, deliveryId, attemptId, PullSettlement.Fail(request.Error), mediator, cancellationToken);
    }

    private static async Task<IResult> Settle(
        ClaimsPrincipal user,
        Guid deliveryId,
        Guid attemptId,
        PullSettlement settlement,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        PullSettlementStatus status = await mediator.Send(
            new SettlePullDeliveryCommand(new PullAttemptOwner(DestinationId(user), deliveryId, attemptId), settlement),
            cancellationToken);
        // AlreadyApplied answers like Applied, so a Subscriber whose first response was lost can
        // repeat the same settlement safely.
        return status == PullSettlementStatus.OwnershipLost ? OwnershipLost() : Results.NoContent();
    }

    private static Guid DestinationId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(SubscriberKeyAuthHandler.DestinationIdClaim)!);

    private static IResult OwnershipLost() => Conflict(
        "ownership_lost",
        "This attempt no longer owns the delivery, so its outcome was not recorded. Stop working on it.");

    private static IResult Conflict(string code, string detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

internal sealed record ClaimRequest(int? Max, int? WaitSeconds);

internal sealed record NackRequest(int? RetryAfterSeconds, string? Error);

internal sealed record FailRequest(string? Error);

internal sealed record ClaimResponse(IReadOnlyList<ClaimedDelivery> Deliveries);

internal sealed record ClaimedDelivery(
    Guid DeliveryId,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    string EventType,
    string? Topic,
    DateTimeOffset AcceptedAt,
    JsonElement Payload,
    DateTimeOffset LeaseExpiresAt,
    string? Traceparent)
{
    public static ClaimedDelivery From(PullDelivery delivery) => new(
        delivery.DeliveryId,
        delivery.AttemptId,
        delivery.AttemptNumber,
        delivery.EventId,
        delivery.EventType,
        delivery.TopicName,
        delivery.AcceptedAt,
        JsonSerializer.Deserialize<JsonElement>(delivery.PayloadJson),
        delivery.LeaseExpiresAt,
        delivery.Traceparent);
}

internal sealed record RenewResponse(DateTimeOffset LeaseExpiresAt);
