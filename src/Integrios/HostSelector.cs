using Integrios.Admin;
using Integrios.Ingestion;
using Integrios.Subscriber;
using Integrios.Worker;

namespace Integrios;

public static class HostSelector
{
    public static readonly IReadOnlyDictionary<string, Func<string[], Task<int>>> Roles =
        new Dictionary<string, Func<string[], Task<int>>>(StringComparer.Ordinal)
        {
            [IngestionHost.Name] = IngestionHost.RunAsync,
            [AdminHost.Name] = AdminHost.RunAsync,
            [WorkerHost.Name] = WorkerHost.RunAsync,
            [SubscriberHost.Name] = SubscriberHost.RunAsync,
        };

    public static Task<int> RunAsync(string[] args, TextWriter error) => RunAsync(args, Roles, error);

    internal static Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, Func<string[], Task<int>>> roles,
        TextWriter error)
    {
        if (args is [var name, .. var rest] && roles.TryGetValue(name, out var run))
            return run(rest);

        error.WriteLine("Usage: integrios <role> [arguments]");
        error.WriteLine($"Roles: {string.Join(", ", roles.Keys)}");
        return Task.FromResult(2);
    }
}
