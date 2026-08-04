namespace Integrios.Admin.Tests;

public sealed class PublicIngressUriTests
{
    [Fact]
    public void Parse_ProductionRejectsHttp()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublicIngressUri.Parse("http://ingress.example.test/root", allowHttp: false));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DevelopmentAllowsHttpAndPreservesProxyPrefix()
    {
        PublicIngressUri uri = PublicIngressUri.Parse(
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
    [InlineData("ftp://ingress.example.test")]
    [InlineData("https://user@ingress.example.test")]
    [InlineData("https://ingress.example.test?query=true")]
    [InlineData("https://ingress.example.test/#fragment")]
    public void Parse_RejectsMissingOrNonOriginValues(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => PublicIngressUri.Parse(value, allowHttp: false));
    }
}
