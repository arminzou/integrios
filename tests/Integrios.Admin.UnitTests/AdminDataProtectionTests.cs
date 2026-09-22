using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Admin.UnitTests;

public sealed class AdminDataProtectionTests
{
    [Fact]
    public void KeyRingPath_IsRequired()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminDataProtection(
                configuration,
                AppContext.BaseDirectory));

        exception.Message.ShouldBe($"{AdminDataProtection.KeyRingPathKey} is required.");
    }

    [Fact]
    public void KeyRingPath_MustBeWritable()
    {
        // Root ignores Unix permission bits, so a read-only directory cannot be simulated there.
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            return;

        DirectoryInfo keyRing = Directory.CreateTempSubdirectory("integrios-key-ring-");
        Action restore = MakeReadOnly(keyRing);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AdminDataProtection.KeyRingPathKey] = keyRing.FullName,
                })
                .Build();

            InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
                new ServiceCollection().AddAdminDataProtection(
                    configuration,
                    AppContext.BaseDirectory));

            exception.Message.ShouldContain(AdminDataProtection.KeyRingPathKey);
        }
        finally
        {
            restore();
            keyRing.Delete(recursive: true);
        }
    }

    private static Action MakeReadOnly(DirectoryInfo directory) =>
        OperatingSystem.IsWindows() ? DenyWrites(directory) : RemoveWriteMode(directory);

    [SupportedOSPlatform("windows")]
    private static Action DenyWrites(DirectoryInfo directory)
    {
        var denyWrite = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles,
            AccessControlType.Deny);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(denyWrite);
        directory.SetAccessControl(security);
        return () =>
        {
            DirectorySecurity restored = directory.GetAccessControl();
            restored.RemoveAccessRule(denyWrite);
            directory.SetAccessControl(restored);
        };
    }

    [UnsupportedOSPlatform("windows")]
    private static Action RemoveWriteMode(DirectoryInfo directory)
    {
        UnixFileMode original = directory.UnixFileMode;
        directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        return () => directory.UnixFileMode = original;
    }
}
