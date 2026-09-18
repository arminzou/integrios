using Integrios.Application.Authoring.Sources;

namespace Integrios.Application.UnitTests.Authoring.Sources;

public sealed class SourceEventTypeDeclarationTests
{
    [Fact]
    public void KeepsTheDeclaredSpellingAndOrder()
    {
        SourceAuthoringValidator.ValidateEventTypes(["Order.Created", "orders/create", "Push Hook"])
            .ShouldBe(["Order.Created", "orders/create", "Push Hook"]);
    }

    [Theory]
    [MemberData(nameof(RefusedDeclarations))]
    public void RefusesDeclarationsNoEventCouldBeRoutedBy(string[]? eventTypes, string expected)
    {
        SourceValidationException exception = Should.Throw<SourceValidationException>(
            () => SourceAuthoringValidator.ValidateEventTypes(eventTypes));

        exception.Field.ShouldBe("event_types");
        exception.Message.ShouldContain(expected);
    }

    public static TheoryData<string[]?, string> RefusedDeclarations => new()
    {
        { null, "at least one" },
        { [], "at least one" },
        { ["order.created", ""], "non-empty" },
        { [" order.created"], "whitespace" },
        { ["order\tcreated"], "control characters" },
        { [new string('a', 201)], "at most 200" },
        { ["order.created", "Order.Created"], "more than once" },
    };
}
