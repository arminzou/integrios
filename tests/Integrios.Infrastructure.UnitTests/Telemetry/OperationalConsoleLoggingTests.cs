using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Integrios.Infrastructure.UnitTests;

public sealed class OperationalConsoleLoggingTests
{
    [Theory]
    [InlineData(false, "json")]
    [InlineData(true, "simple")]
    public void Registration_SelectsTheEnvironmentFormatter(bool isDevelopment, string formatterName)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOperationalConsoleLogging(isDevelopment));

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName
            .ShouldBe(formatterName);
    }
}
