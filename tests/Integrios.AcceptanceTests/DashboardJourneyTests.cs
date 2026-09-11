using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

/// The dashboard's golden authoring journey, driven in a real browser against this packaged
/// deployment. The frontend suite runs the same journey only when a deployment is named, and there a
/// skipped run reports the same as a passing one; here it runs on every Acceptance gate, and a
/// journey that did not execute fails rather than passing quietly.
[Collection(PackagedDeploymentCollection.Name)]
public sealed class DashboardJourneyTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan JourneyTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task GoldenAuthoringJourney_IsAcceptedByThePackagedAdminApi()
    {
        string frontend = Path.Combine(fixture.RepoRoot, "src", "Integrios.Admin", "frontend");
        string vitest = Path.Combine(frontend, "node_modules", "vitest", "vitest.mjs");
        File.Exists(vitest).ShouldBeTrue(
            "The golden journey needs the dashboard's dependencies: run `npm ci` and "
            + "`npx playwright install chromium` in src/Integrios.Admin/frontend.");

        string report = Path.Combine(Path.GetTempPath(), $"integrios-journey-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = frontend,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in (string[])[
            vitest,
            "run",
            "tests/e2e/journey.browser.test.ts",
            "--reporter=default",
            "--reporter=json",
            $"--outputFile.json={report}"])
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["INTEGRIOS_JOURNEY_ORIGIN"] = fixture.AdminClient.BaseAddress!.GetLeftPart(UriPartial.Authority);
        startInfo.Environment["INTEGRIOS_JOURNEY_OPERATOR_KEY"] = fixture.AdminAuthorization;
        startInfo.Environment["NO_COLOR"] = "1";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The golden journey needs Node on the PATH.", exception);
        }

        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(JourneyTimeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException($"The golden journey did not finish within {JourneyTimeout}.");
            }

            string output = $"{await stdout}\n{await stderr}";
            process.ExitCode.ShouldBe(0, $"The golden journey failed:\n{output}");

            // A zero exit also covers a suite that skipped itself, so what ran is read off the report.
            using JsonDocument results = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            JsonElement root = results.RootElement;
            root.GetProperty("numPendingTests").GetInt32().ShouldBe(0, $"The golden journey was skipped:\n{output}");
            root.GetProperty("numPassedTests").GetInt32().ShouldBeGreaterThan(0, $"The golden journey ran no tests:\n{output}");
        }

        File.Delete(report);
    }
}
