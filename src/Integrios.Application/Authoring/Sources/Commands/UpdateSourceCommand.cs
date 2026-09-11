using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Application.Transforms;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record UpdateSourceCommand(
    Guid TenantId,
    Guid Id,
    JsonElement Configuration,
    SourceVerificationInput? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping) : IRequest<SourceDto?>;

internal sealed class UpdateSourceCommandHandler(
    ISourceRepository sourceRepository,
    IConnectorReader connectorReader,
    ITransformEvaluator evaluator)
    : IRequestHandler<UpdateSourceCommand, SourceDto?>
{
    public async Task<SourceDto?> Handle(UpdateSourceCommand command, CancellationToken cancellationToken)
    {
        Source? source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (source is null || source.Status != Domain.Enums.SourceStatus.Active)
            return null;
        Connector connector = await connectorReader.GetByIdAsync(source.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("The Source's Connector does not exist.");
        SourceVerificationInput? verification = command.Verification ?? ToInput(source.Verification);
        SourceAuthoringValidator.Validate(source.Type, command.Configuration, verification, connector);
        SourceAuthoringValidator.ValidateRuntimeContract(
            source.Type, command.InputRequirements, command.Mapping, source.EventIdentityRule, evaluator);
        JsonElement configuration = source.Type == Domain.Enums.SourceType.Webhook
            ? WebhookCallbackConfiguration.WithCallbackId(
                command.Configuration,
                WebhookCallbackConfiguration.ExistingOrNew(source.Configuration))
            : command.Configuration.Clone();
        Source? updated = await sourceRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            configuration,
            SourceAuthoringValidator.ToVerification(verification),
            command.InputRequirements?.Clone(),
            command.Mapping,
            cancellationToken);
        return updated is null ? null : SourceDto.From(updated);
    }

    private static SourceVerificationInput? ToInput(SourceVerification? verification) => verification is null ? null : new()
    {
        Scheme = verification.Scheme,
        Config = verification.Config,
        SecretRefs = verification.SecretRefs,
    };
}
