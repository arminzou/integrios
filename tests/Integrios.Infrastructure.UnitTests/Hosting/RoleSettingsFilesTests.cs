using Integrios.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Integrios.Infrastructure.UnitTests.Hosting;

public sealed class RoleSettingsFilesTests : IDisposable
{
    private readonly DirectoryInfo contentRoot = Directory.CreateTempSubdirectory("integrios-role-settings-");

    public void Dispose() => contentRoot.Delete(recursive: true);

    [Fact]
    public void RoleFiles_SitBetweenTheSharedFilesAndBelowArguments()
    {
        Write("appsettings.json", """{ "Shared": "base", "Role": "base", "Env": "base", "RoleEnv": "base", "Arg": "base" }""");
        Write("appsettings.worker.json", """{ "Role": "role", "Env": "role", "RoleEnv": "role", "Arg": "role" }""");
        Write("appsettings.Staging.json", """{ "Env": "env", "RoleEnv": "env", "Arg": "env" }""");
        Write("appsettings.worker.Staging.json", """{ "RoleEnv": "role-env", "Arg": "role-env" }""");
        Write("appsettings.admin.json", """{ "Shared": "another role's file" }""");

        IConfiguration configuration = Build("worker", "Staging", "--Arg=arg");

        configuration["Shared"].ShouldBe("base");
        configuration["Role"].ShouldBe("role");
        configuration["Env"].ShouldBe("env");
        configuration["RoleEnv"].ShouldBe("role-env");
        configuration["Arg"].ShouldBe("arg");
    }

    [Fact]
    public void MissingRoleFiles_LeaveTheSharedFilesInEffect()
    {
        Write("appsettings.json", """{ "Shared": "base" }""");

        IConfiguration configuration = Build("ingestion", "Production");

        configuration["Shared"].ShouldBe("base");
    }

    private IConfiguration Build(string role, string environment, params string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot.FullName,
            EnvironmentName = environment,
            Args = args,
        });
        builder.AddRoleSettingsFiles(role);
        return builder.Configuration;
    }

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(contentRoot.FullName, name), json);
}
