using Microsoft.Extensions.Configuration;

namespace Integrios.Worker;

internal sealed record FanoutLoopOptions(int BatchSize, TimeSpan IdlePollInterval)
{
    internal static FanoutLoopOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new FanoutLoopOptions(
            WorkerLoopOptionsReader.ReadPositiveInt(
                configuration,
                "Integrios:Worker:FanoutLoop:BatchSize",
                10),
            WorkerLoopOptionsReader.ReadDuration(
                configuration,
                "Integrios:Worker:FanoutLoop:IdlePollInterval",
                TimeSpan.FromSeconds(2)));

        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (BatchSize <= 0)
            throw new InvalidOperationException("Integrios:Worker:FanoutLoop:BatchSize must be positive.");
        if (IdlePollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Integrios:Worker:FanoutLoop:IdlePollInterval must be positive.");
    }
}

internal sealed record DeliveryLoopOptions(int BatchSize, TimeSpan IdlePollInterval)
{
    internal static DeliveryLoopOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new DeliveryLoopOptions(
            WorkerLoopOptionsReader.ReadPositiveInt(
                configuration,
                "Integrios:Worker:DeliveryLoop:BatchSize",
                25),
            WorkerLoopOptionsReader.ReadDuration(
                configuration,
                "Integrios:Worker:DeliveryLoop:IdlePollInterval",
                TimeSpan.FromSeconds(2)));

        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (BatchSize <= 0)
            throw new InvalidOperationException("Integrios:Worker:DeliveryLoop:BatchSize must be positive.");
        if (IdlePollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Integrios:Worker:DeliveryLoop:IdlePollInterval must be positive.");
    }
}

internal sealed record HistoryRetentionOptions(TimeSpan Period)
{
    internal const string ConfigurationKey = "Integrios:Worker:HistoryRetention:Period";
    internal const int BatchSize = 500;
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MinimumPeriod = TimeSpan.FromDays(7);

    internal static HistoryRetentionOptions? FromConfiguration(IConfiguration configuration)
    {
        string? configured = configuration[ConfigurationKey];
        if (configured is null)
            return null;
        if (!TimeSpan.TryParse(configured, out TimeSpan period))
            throw new InvalidOperationException($"{ConfigurationKey} must be a TimeSpan value.");
        if (period < MinimumPeriod)
            throw new InvalidOperationException($"{ConfigurationKey} must be at least seven days.");

        return new(period);
    }
}

internal static class WorkerLoopOptionsReader
{
    internal static int ReadPositiveInt(IConfiguration configuration, string key, int fallback)
    {
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        return int.TryParse(configured, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be an integer value.");
    }

    internal static TimeSpan ReadDuration(IConfiguration configuration, string key, TimeSpan fallback)
    {
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        return TimeSpan.TryParse(configured, out TimeSpan parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be a TimeSpan value.");
    }
}
