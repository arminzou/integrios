namespace Integrios.Admin.UnitTests;

public sealed class PublicIngestionBaseUriTests
{
    [Fact]
    public void Parse_ProductionRejectsHttp()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublicIngestionBaseUri.Parse("http://ingestion.example.test/root", allowHttp: false));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DevelopmentAllowsHttpAndPreservesProxyPrefix()
    {
        PublicIngestionBaseUri uri = PublicIngestionBaseUri.Parse(
            "http://localhost:5231/proxy/root/",
            allowHttp: true);

        Assert.Equal(
            "http://localhost:5231/proxy/root/webhooks/github/00000000-0000-0000-0000-000000000001",
            uri.AppendCallbackPath("/webhooks/github/00000000-0000-0000-0000-000000000001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://ingestion.example.test")]
    [InlineData("https://user@ingestion.example.test")]
    [InlineData("https://ingestion.example.test?query=true")]
    [InlineData("https://ingestion.example.test/#fragment")]
    public void Parse_RejectsMissingOrNonOriginValues(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => PublicIngestionBaseUri.Parse(value, allowHttp: false));
    }
}
