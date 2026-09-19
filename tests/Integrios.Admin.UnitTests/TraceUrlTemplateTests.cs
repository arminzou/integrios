namespace Integrios.Admin.UnitTests;

public sealed class TraceUrlTemplateTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_AbsentTemplateResolvesNoLink(string? value)
    {
        TraceUrlTemplate.Parse(value).Resolve(TraceId).ShouldBeNull();
    }

    [Theory]
    [InlineData("https://tracing.example.test/trace/{trace_id}", "https://tracing.example.test/trace/" + TraceId)]
    [InlineData("http://localhost:16686/trace/{trace_id}?uiFind=x", "http://localhost:16686/trace/" + TraceId + "?uiFind=x")]
    [InlineData("https://grafana.example.test/explore?left={\"query\":\"{trace_id}\"}#panel", "https://grafana.example.test/explore?left={\"query\":\"" + TraceId + "\"}#panel")]
    public void Parse_ValidTemplateSubstitutesTheTraceId(string value, string expected)
    {
        TraceUrlTemplate template = TraceUrlTemplate.Parse(value);

        template.Resolve(TraceId).ShouldBe(expected);
        template.Resolve(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("https://tracing.example.test/trace/")]
    [InlineData("https://tracing.example.test/trace/{TRACE_ID}")]
    [InlineData("https://tracing.example.test/{trace_id}/{trace_id}")]
    [InlineData("/trace/{trace_id}")]
    [InlineData("ftp://tracing.example.test/{trace_id}")]
    [InlineData("javascript:alert('{trace_id}')")]
    [InlineData("https://user:secret@tracing.example.test/{trace_id}")]
    public void Parse_RejectsMalformedTemplates(string value)
    {
        Should.Throw<InvalidOperationException>(() => TraceUrlTemplate.Parse(value))
            .Message.ShouldContain(TraceUrlTemplate.ConfigurationKey, Case.Sensitive);
    }
}
