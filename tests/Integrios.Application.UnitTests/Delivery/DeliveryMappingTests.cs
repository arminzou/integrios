using Integrios.Application.Delivery;
using Integrios.Application.Transforms;
using NSubstitute;

namespace Integrios.Application.UnitTests;

public sealed class DeliveryMappingTests
{
    private const string Mapping = """{"engine":"jsonata","version":"1","expression":"$"}""";
    private static readonly TransformContext Context = new("order.created", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Apply_ReportsAMalformedMappingSnapshotAsATransformFailure()
    {
        (string? payload, string? error) = DeliveryMapping.Apply(
            Substitute.For<ITransformEvaluator>(), "{not json", "{}", Context);

        payload.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void Apply_ReportsAMappingSnapshotWithAWrongFieldTypeAsATransformFailure()
    {
        (string? payload, string? error) = DeliveryMapping.Apply(
            Substitute.For<ITransformEvaluator>(), """{"engine":42,"version":"1","expression":"$"}""", "{}", Context);

        payload.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void Apply_ReportsAMappingSnapshotWithAnUndecodableStringAsATransformFailure()
    {
        (string? payload, string? error) = DeliveryMapping.Apply(
            Substitute.For<ITransformEvaluator>(), """{"engine":"jsonata","version":"1","expression":"\uD800"}""", "{}", Context);

        payload.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void Apply_LetsAProgrammingErrorPropagateInsteadOfReportingATransformFailure()
    {
        var evaluator = Substitute.For<ITransformEvaluator>();
        evaluator.Evaluate(Arg.Any<TransformSpec>(), Arg.Any<string>(), Arg.Any<TransformContext>())
            .Returns(_ => throw new NullReferenceException("bug"));

        Should.Throw<NullReferenceException>(() => DeliveryMapping.Apply(evaluator, Mapping, "{}", Context));
    }
}
