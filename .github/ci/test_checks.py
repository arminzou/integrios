import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from checks import CHECKS, select_checks, validate_results


class TestProjectTests(unittest.TestCase):
    def test_lists_unit_and_architecture_projects_from_solution(self):
        script = Path(__file__).resolve().with_name("checks.py")
        result = subprocess.run(
            [sys.executable, str(script), "test-projects"],
            cwd=script.parents[2], capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        paths = result.stdout.splitlines()
        self.assertEqual(paths, sorted(set(paths)))
        self.assertTrue(all(path.startswith("tests/") for path in paths))
        for name in (
            "Integrios.Admin.UnitTests", "Integrios.Application.UnitTests", "Integrios.ArchitectureTests",
            "Integrios.Client.UnitTests", "Integrios.Domain.UnitTests", "Integrios.Infrastructure.UnitTests",
            "Integrios.Ingestion.UnitTests", "Integrios.UnitTests", "Integrios.Worker.UnitTests",
        ):
            self.assertIn(f"tests/{name}/{name}.csproj", paths)
        for name in ("Integrios.FunctionalTests", "Integrios.AcceptanceTests", "Integrios.Tests.Shared"):
            self.assertNotIn(f"tests/{name}/{name}.csproj", paths)


class SelectionTests(unittest.TestCase):
    def assert_selected(self, paths, *expected):
        result = select_checks(paths, "pull_request")
        self.assertEqual({name for name, enabled in result.items() if enabled}, {"helpers", *expected})

    def test_lightweight_paths(self):
        self.assert_selected(["README.md"], "docs")
        self.assert_selected(["docs/.vitepress/config.mts"], "docs")
        self.assert_selected([".claude/skills/verify-integrios/scripts/control.sh"])
        self.assert_selected(["src/Integrios.Admin/frontend/src/App.tsx"], "frontend")
        self.assert_selected(["src/Integrios.Admin/frontend/package-lock.json"], "frontend")

    def test_backend_and_contract_paths(self):
        self.assert_selected(["src/Integrios.Worker/WorkerHost.cs"], "verify", "postgres")
        self.assert_selected(["src/Integrios.Ingestion/Endpoints/Events.cs"], "verify", "postgres")
        self.assert_selected(["src/Integrios.Admin/Endpoints/Tenants.cs"], "verify", "frontend", "postgres")
        self.assert_selected(["src/Integrios.Application/Authoring/TenantDto.cs"], "verify", "frontend", "postgres")

    def test_provider_sensitive_paths(self):
        for path in (
            "src/Integrios.Infrastructure/Persistence/Queries.cs",
            "src/Integrios.Domain/Entities/Event.cs",
            "src/Integrios.Migrations.SqlServer/Migrations/AddColumn.cs",
            "tests/Integrios.FunctionalTests/EventsTests.cs",
        ):
            with self.subTest(path=path):
                self.assert_selected([path], "verify", "frontend", "postgres", "sqlserver")

    def test_unknown_tooling_and_dependencies_select_everything(self):
        for path in (
            "new-package/file.cs", "src/Integrios/HostSelector.cs", "Dockerfile",
            "compose.yml", "global.json", "src/Integrios.Worker/Integrios.Worker.csproj",
            ".github/workflows/ci.yml", ".github/ci/checks.py", "package-lock.json",
            "src/Integrios.Worker/appsettings.worker.json", "deploy/compose.yml",
        ):
            with self.subTest(path=path):
                self.assertEqual(select_checks([path], "pull_request"), dict.fromkeys(CHECKS, True))

    def test_mixed_changes_union_jobs(self):
        self.assert_selected(["README.md", "src/Integrios.Worker/WorkerHost.cs"], "docs", "verify", "postgres")

    def test_non_pr_and_empty_diff_are_comprehensive(self):
        for event in ("push", "schedule", "workflow_dispatch"):
            self.assertEqual(select_checks(["README.md"], event, True), dict.fromkeys(CHECKS, True))
        self.assertEqual(select_checks([], "pull_request"), dict.fromkeys(CHECKS, True))

    def test_release_metadata_skips_only_application_jobs(self):
        self.assert_selected(["CHANGELOG.md"], "docs")
        plan = select_checks(["deploy/compose.yml"], "pull_request", True)
        self.assertEqual({name for name, enabled in plan.items() if enabled}, {"docs", "helpers"})
        self.assertEqual(
            select_checks(["VERSION", "new-package/file.cs"], "pull_request", True),
            dict.fromkeys(CHECKS, True),
        )

    def test_real_diff_includes_deleted_and_renamed_paths(self):
        script = Path(__file__).with_name("checks.py").resolve()
        with tempfile.TemporaryDirectory() as directory:
            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args])
            git("init", "-q")
            original = Path(directory, "src/Integrios.Infrastructure/old file.cs")
            original.parent.mkdir(parents=True)
            original.write_text("example", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-qm", "base")
            base = git("rev-parse", "HEAD").decode().strip()
            original.rename(Path(directory, "README.md"))
            git("add", "-A")
            git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-qm", "rename")
            output = Path(directory, "outputs")
            env = dict(os.environ, GITHUB_EVENT_NAME="pull_request", BASE_SHA=base, GITHUB_OUTPUT=str(output), RELEASE_PR="false")
            subprocess.run([sys.executable, str(script), "select"], cwd=directory, env=env, check=True, capture_output=True)
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
            self.assertEqual(json.loads(values["providers"]), ["postgres", "sqlserver"])
            self.assertEqual(values["docs"], "true")

    def test_shallow_checkout_fetches_exact_event_base_after_base_advances(self):
        script = Path(__file__).with_name("checks.py").resolve()
        with tempfile.TemporaryDirectory() as directory:
            origin = Path(directory, "origin")
            checkout = Path(directory, "checkout")
            origin.mkdir()

            def git(*args):
                return subprocess.check_output(["git", "-C", str(origin), *args])

            git("init", "-q")
            readme = origin / "README.md"
            readme.write_text("base", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-qm", "event base")
            base = git("rev-parse", "HEAD").decode().strip()
            backend = origin / "src/Integrios.Infrastructure/Queries.cs"
            backend.parent.mkdir(parents=True)
            backend.write_text("changed after event base", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-qm", "advanced base")
            readme.write_text("updated", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-qm", "checked out head")
            subprocess.run(["git", "clone", "--depth=2", origin.as_uri(), str(checkout)], check=True, capture_output=True)
            missing = subprocess.run(["git", "cat-file", "-e", base], cwd=checkout, capture_output=True)
            self.assertNotEqual(missing.returncode, 0)
            output = Path(directory, "outputs")
            env = dict(os.environ, GITHUB_EVENT_NAME="pull_request", BASE_SHA=base, GITHUB_OUTPUT=str(output), RELEASE_PR="false")

            result = subprocess.run([sys.executable, str(script), "select"], cwd=checkout, env=env, capture_output=True)

            self.assertEqual(result.returncode, 0, result.stderr.decode())
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
            self.assertEqual(json.loads(values["providers"]), ["postgres", "sqlserver"])
            self.assertEqual(values["docs"], "true")


class GateTests(unittest.TestCase):
    def setUp(self):
        self.plan = select_checks(["README.md"], "pull_request")
        self.needs = {job: {"result": result} for job, result in {
            "changes": "success", "verify": "skipped", "frontend": "skipped",
            "functional": "skipped", "docs": "success", "helpers": "success",
        }.items()}

    def test_expected_skips_pass(self):
        self.assertEqual(validate_results(self.plan, self.needs), [])

    def test_required_failure_cancellation_skip_or_missing_blocks(self):
        for result in ("failure", "cancelled", "skipped", "missing"):
            with self.subTest(result=result):
                self.needs["docs"]["result"] = result
                self.assertTrue(validate_results(self.plan, self.needs))

    def test_failed_selection_blocks_even_with_green_checks(self):
        self.needs["changes"]["result"] = "failure"
        self.assertTrue(validate_results(self.plan, self.needs))

    def test_both_provider_matrix_must_succeed(self):
        self.plan = dict.fromkeys(CHECKS, True)
        for job in self.needs:
            self.needs[job]["result"] = "success"
        self.assertEqual(validate_results(self.plan, self.needs), [])
        for result in ("failure", "cancelled", "skipped"):
            self.needs["functional"]["result"] = result
            self.assertTrue(validate_results(self.plan, self.needs))


if __name__ == "__main__":
    unittest.main()
