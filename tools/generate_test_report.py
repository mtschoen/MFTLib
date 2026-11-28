"""Generate the main-branch test summary from CI coverage artifacts."""

import argparse
import html
import re
from collections import Counter
from datetime import UTC, datetime, timedelta
from decimal import ROUND_HALF_UP, Decimal, localcontext
from pathlib import Path
from xml.etree import ElementTree


def test_counts(root):
    counters = root.findall("{*}ResultSummary/{*}Counters")
    if root.tag.rsplit("}", 1)[-1] != "TestRun" or len(counters) != 1:
        raise ValueError("Missing or duplicate TRX Counters")
    counts = {}
    for name in ("total", "passed", "failed"):
        text = counters[0].get(name, "")
        if not re.fullmatch(r"[0-9]+", text) or int(text) > 2**63 - 1:
            raise ValueError(f"Invalid TRX counter: {name}")
        counts[name] = int(text)

    outcomes = Counter()
    for result in root.findall("{*}Results/{*}UnitTestResult"):
        outcome = result.get("outcome", "").casefold()
        if outcome not in {"passed", "failed", "notexecuted"}:
            raise ValueError(f"Unsupported TRX result outcome: {outcome}")
        outcomes[outcome] += 1
    if (sum(outcomes.values()), outcomes["passed"], outcomes["failed"]) != (
        counts["total"],
        counts["passed"],
        counts["failed"],
    ):
        raise ValueError("TRX counters do not reconcile with result outcomes")
    return (
        counts["total"],
        counts["passed"],
        counts["failed"],
        outcomes["notexecuted"],
    )


def test_project(root):
    projects = {}
    for definition in root.findall("{*}TestDefinitions/{*}UnitTest"):
        filename = definition.get("storage", "").replace("\\", "/").rsplit("/", 1)[-1]
        project = filename.rsplit(".", 1)[0]
        projects.setdefault(project.casefold(), project)
    if len(projects) != 1 or not next(iter(projects)).strip():
        raise ValueError(
            "TRX must identify exactly one test project in UnitTest storage"
        )
    return next(iter(projects.values()))


def coverage_percentage(text):
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", text) or not 0 <= Decimal(text) <= 1:
        raise ValueError("Invalid Cobertura rate")
    with localcontext() as context:
        context.rounding = ROUND_HALF_UP
        return f"{Decimal(text) * 100:.2f}%"


def coverage_values(path):
    root = ElementTree.parse(path).getroot()
    if root.tag != "coverage":
        raise ValueError("Missing Cobertura coverage root")
    return [
        coverage_percentage(root.get(name, "")) for name in ("line-rate", "branch-rate")
    ]


def markdown_row(*values):
    cells = [
        html.escape(str(value), quote=False)
        .replace("|", "&#124;")
        .replace("\r", " ")
        .replace("\n", " ")
        for value in values
    ]
    return "| " + " | ".join(cells) + " |"


def artifact_root(directory, name, commit):
    markers = [path for path in directory.rglob("commit.txt") if path.is_file()]
    if (
        len(markers) != 1
        or markers[0].read_text(encoding="utf-8-sig").strip() != commit
    ):
        raise ValueError(f"{name} artifact source commit mismatch")
    return markers[0].parent


def platform_lines(name, results, coverage):
    root = ElementTree.parse(results).getroot()
    counts = test_counts(root)
    return [
        f"## {name}",
        "",
        "| Test project | Total | Passed | Failed | Skipped |",
        "| --- | ---: | ---: | ---: | ---: |",
        markdown_row(test_project(root), *counts),
        "",
        "| Managed coverage | Line | Branch |",
        "| --- | ---: | ---: |",
        markdown_row(coverage.name, *coverage_values(coverage)),
        "",
    ]


def native_lines(path):
    if not path.exists():
        return []
    measurements = [
        line.strip()
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if re.fullmatch(
            r"lines:\s+[0-9]+(?:\.[0-9]+)?%\s+\([0-9]+ out of [0-9]+\)\s*",
            line,
            re.IGNORECASE,
        )
    ]
    if len(measurements) != 1:
        raise ValueError("Missing or duplicate native line coverage summary")
    return [f"Native coverage: {measurements[0]}", ""]


def utc_timestamp(text):
    if not re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
        r"(?:\.[0-9]+)?(?:Z|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])",
        text,
        re.IGNORECASE,
    ):
        raise ValueError(
            "An ISO 8601 run timestamp with an explicit offset is required"
        )
    timestamp = datetime.fromisoformat(text.upper())
    if abs(timestamp.utcoffset()) > timedelta(hours=14):
        raise ValueError("Invalid ISO 8601 offset")
    return (
        timestamp.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")
    )


def generate_report(arguments):
    if not re.fullmatch(r"[0-9a-f]{40}", arguments.commit):
        raise ValueError("Expected a full source commit SHA")
    if not re.fullmatch(r"[0-9]+", arguments.run_id):
        raise ValueError("Expected a numeric workflow run id")
    timestamp = utc_timestamp(arguments.generated_at)
    linux = artifact_root(arguments.linux_artifact, "Linux", arguments.commit)
    if arguments.windows_skipped:
        windows_lines = ["## Windows unelevated", "", "Windows did not run.", ""]
    else:
        windows = artifact_root(arguments.windows_artifact, "Windows", arguments.commit)
        windows_lines = platform_lines(
            "Windows unelevated",
            windows / "non-admin.trx",
            windows.parent / "coverage.xml",
        )

    lines = [
        "# Test report: generated on main by CI; must not be edited in a pull request",
        "",
        f"Source commit: {arguments.commit}",
        f"Workflow run: {arguments.run_id}",
        f"Generated at: {timestamp} (workflow run creation time).",
        "",
        *windows_lines,
        *platform_lines(
            "Linux", linux / "managed.trx", linux / "managed/coverage.cobertura.xml"
        ),
        *native_lines(linux / "summary.txt"),
        "Full TRX and coverage reports are retained as CI artifacts.",
        "",
    ]
    content = "\n".join(lines).encode("utf-8")
    if len(content) >= 4096:
        raise ValueError("Test report exceeds the 4 KB summary budget")
    if "\u2014".encode() in content:
        raise ValueError("Test report contains an em dash")
    arguments.output_path.write_bytes(content)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    windows = parser.add_mutually_exclusive_group(required=True)
    windows.add_argument("--windows-artifact", type=Path)
    windows.add_argument("--windows-skipped", action="store_true")
    parser.add_argument("--linux-artifact", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--generated-at", required=True)
    parser.add_argument("--output-path", type=Path, required=True)
    try:
        generate_report(parser.parse_args(argv))
    except (ValueError, OverflowError, OSError, ElementTree.ParseError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
