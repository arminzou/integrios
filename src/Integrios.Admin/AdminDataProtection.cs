using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Admin;

internal static class AdminDataProtection
{
    internal const string KeyRingPathKey = "Integrios:Admin:DataProtection:KeyRingPath";
    private const string ApplicationName = "Integrios.Admin";

    public static IServiceCollection AddAdminDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        string? configuredPath = configuration[KeyRingPathKey];
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException($"{KeyRingPathKey} is required.");

        DirectoryInfo keyRing = RequireWritableDirectory(Path.GetFullPath(configuredPath, contentRootPath));
        services.AddDataProtection()
            .PersistKeysToFileSystem(keyRing)
            .SetApplicationName(ApplicationName);

        return services;
    }

    // Readiness only checks the database, so an unwritable key ring would otherwise surface as a
    // failed first sign-in rather than a failed start.
    private static DirectoryInfo RequireWritableDirectory(string path)
    {
        try
        {
            DirectoryInfo directory = Directory.CreateDirectory(path);
            string probe = Path.Combine(directory.FullName, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{KeyRingPathKey} '{path}' must be a writable directory.", exception);
        }
    }
}
