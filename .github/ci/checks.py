"""Select whole CI jobs and validate their results, using only the standard library."""

import json
import os
import subprocess
import sys


CHECKS = ("verify", "frontend", "postgres", "sqlserver", "docs", "helpers")


def select_checks(paths, event, release_pr=False):
    if event != "pull_request" or not paths:
        return dict.fromkeys(CHECKS, True)
    if release_pr:
        # Release metadata renders already-verified commits; the tag still runs every gate.
        return {name: name in ("docs", "helpers") for name in CHECKS}

    selected = dict.fromkeys(CHECKS, False)
    selected["helpers"] = True
    for path in paths:
        if path.startswith(".claude/skills/verify-integrios/"):
            continue
        if path.startswith("docs/") or path.endswith(".md") or path == "LICENSE":
            selected["docs"] = True
        elif path.startswith("src/Integrios.Admin/frontend/"):
            selected["frontend"] = True
        elif path.endswith((".csproj", ".props", ".targets", "packages.lock.json")) or "/appsettings" in path:
            return dict.fromkeys(CHECKS, True)
        elif path.startswith(("src/Integrios.Admin/", "src/Integrios.Application/")):
            selected.update(verify=True, frontend=True, postgres=True)
        elif path.startswith(("src/Integrios.Infrastructure/", "src/Integrios.Domain/", "src/Integrios.Migrations.")):
            selected.update(verify=True, frontend=True, postgres=True, sqlserver=True)
        elif path.startswith(("src/Integrios.Ingestion/", "src/Integrios.Worker/")):
            selected.update(verify=True, postgres=True)
        elif path.startswith("tests/") and path.endswith(".cs"):
            selected.update(verify=True, frontend=True, postgres=True, sqlserver=True)
        else:
            # New paths, tooling, deployment and dependency changes get every PR check.
            return dict.fromkeys(CHECKS, True)
    return selected


def validate_results(plan, needs):
    expected = {
        "verify": plan["verify"],
        "frontend": plan["frontend"],
        "functional": plan["postgres"] or plan["sqlserver"],
        "docs": plan["docs"],
        "helpers": plan["helpers"],
    }
    errors = []
    if needs.get("changes", {}).get("result") != "success":
        errors.append("change detection did not succeed")
    for job, required in expected.items():
        wanted = "success" if required else "skipped"
        actual = needs.get(job, {}).get("result", "missing")
        if actual != wanted:
            errors.append(f"{job}: expected {wanted}, got {actual}")
    return errors


def main():
    if sys.argv[1:] == ["select"]:
        event = os.environ["GITHUB_EVENT_NAME"]
        paths = []
        if event == "pull_request":
            changed = subprocess.check_output([
                "git", "diff", "--name-only", "--no-renames", "-z",
                os.environ["BASE_SHA"], "HEAD", "--",
            ])
            paths = [os.fsdecode(path) for path in changed.split(b"\0") if path]
        plan = select_checks(paths, event, os.environ.get("RELEASE_PR") == "true")
        providers = [name for name in ("postgres", "sqlserver") if plan[name]]
        outputs = {name: str(value).lower() for name, value in plan.items()}
        outputs["providers"] = json.dumps(providers or ["postgres"])
        outputs["plan"] = json.dumps(plan)
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for name, value in outputs.items():
                output.write(f"{name}={value}\n")
        print(json.dumps({"paths": paths, "checks": plan}, indent=2))
    elif sys.argv[1:] == ["gate"]:
        errors = validate_results(json.loads(os.environ["PLAN"]), json.loads(os.environ["NEEDS"]))
        if errors:
            raise SystemExit("\n".join(errors))
        print("All selected checks passed; only unselected jobs were skipped.")
    else:
        raise SystemExit("usage: checks.py <select|gate>")


if __name__ == "__main__":
    main()
