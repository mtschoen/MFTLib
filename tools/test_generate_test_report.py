import contextlib
import importlib.util
import io
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

GENERATOR = Path(__file__).with_name("generate_test_report.py").resolve()
SPECIFICATION = importlib.util.spec_from_file_location("generate_test_report", GENERATOR)
generator = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(generator)
COMMIT = "0123456789abcdef0123456789abcdef01234567"
HEADER = (
    "# Test report: generated on main by CI; must not be edited in a pull request\n\n"
    f"Source commit: {COMMIT}\nWorkflow run: 42\n"
    "Generated at: 2026-10-24T12:34:56Z (workflow run creation time).\n\n"
)
TEST_TABLE = (
    "| Test project | Total | Passed | Failed | Skipped |\n"
    "| --- | ---: | ---: | ---: | ---: |\n"
    "| MFTLib.Tests | 12 | 9 | 1 | 2 |\n\n"
    "| Managed coverage | Line | Branch |\n| --- | ---: | ---: |\n"
)
WINDOWS_SECTION = (
    "## Windows unelevated\n\n" + TEST_TABLE + "| coverage.xml | 97.35% | 93.92% |\n\n"
)
LINUX_SECTION = (
    "## Linux\n\n" + TEST_TABLE + "| coverage.cobertura.xml | 92.76% | 90.68% |\n\n"
)
NATIVE = "Native coverage: lines: 81.2% (812 out of 1000)\n\n"
FOOTER = "Full TRX and coverage reports are retained as CI artifacts.\n"


def test_results(passed=9, failed=1, skipped=2):
    outcomes = "".join(
        f'<UnitTestResult outcome="{outcome}"/>' * count
        for outcome, count in (
            ("Passed", passed),
            ("Failed", failed),
            ("NotExecuted", skipped),
        )
    )
    return (
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        '<TestDefinitions><UnitTest storage="build\\MFTLib.Tests.dll">'
        '<TestMethod codeBase="AnotherName.dll"/></UnitTest></TestDefinitions>'
        f'<Results>{outcomes}</Results><ResultSummary><Counters total="{passed + failed + skipped}" '
        f'passed="{passed}" failed="{failed}" notExecuted="0"/></ResultSummary></TestRun>'
    )


class GenerateTestReportTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.windows = self.root / "windows"
        self.linux = self.root / "linux"
        self.windows_report = self.windows / "coverage-report"
        self.windows_report.mkdir(parents=True)
        (self.linux / "managed").mkdir(parents=True)
        for artifact in (self.windows_report, self.linux):
            (artifact / "commit.txt").write_text(COMMIT + "\n", encoding="utf-8-sig")
        (self.windows_report / "non-admin.trx").write_text(
            test_results(), encoding="utf-8"
        )
        (self.windows / "coverage.xml").write_text(
            '<coverage line-rate="0.9735" branch-rate="0.9392"/>', encoding="utf-8"
        )
        self.linux_results = self.linux / "managed.trx"
        self.linux_results.write_text(test_results(), encoding="utf-8")
        self.linux_coverage = self.linux / "managed/coverage.cobertura.xml"
        self.linux_coverage.write_text(
            '<coverage line-rate="0.9276" branch-rate="0.9068"/>', encoding="utf-8"
        )
        self.native = self.linux / "summary.txt"
        self.native.write_text(
            "ignored\r\nlines: 81.2% (812 out of 1000)\r\n", encoding="utf-8"
        )
        self.output = self.root / "report.md"

    def generate(self, windows_skipped=False, **provenance):
        options = {
            "commit": COMMIT,
            "run-id": "42",
            "generated-at": "2026-10-24T14:34:56.123+02:00",
        }
        options.update(provenance)
        arguments = (
            ["--windows-skipped"]
            if windows_skipped
            else ["--windows-artifact", str(self.windows)]
        )
        for option, value in options.items():
            arguments.extend((f"--{option}", value))
        command = [
            *arguments,
            "--linux-artifact",
            str(self.linux),
            "--output-path",
            str(self.output),
        ]
        return self.run_in_process(command)

    def run_in_process(self, command):
        stderr = io.StringIO()
        returncode = 0
        with contextlib.redirect_stderr(stderr):
            try:
                generator.main(command)
            except SystemExit as error:
                returncode = error.code
        return subprocess.CompletedProcess(command, returncode, "", stderr.getvalue())

    def assert_report(self, expected, **options):
        result = self.generate(**options)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.output.read_bytes(), expected.encode("utf-8"))

    def assert_rejected(self, message, **options):
        result = self.generate(**options)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)
        self.assertFalse(self.output.exists())

    def test_command_line_runs_as_a_process(self):
        result = subprocess.run(
            [
                sys.executable,
                str(GENERATOR),
                "--windows-skipped",
                "--linux-artifact",
                str(self.linux),
                "--commit",
                COMMIT,
                "--run-id",
                "42",
                "--generated-at",
                "2026-10-24T14:34:56.123+02:00",
                "--output-path",
                str(self.output),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Windows did not run.", self.output.read_text(encoding="utf-8"))
        rejected = subprocess.run(
            [sys.executable, str(GENERATOR), "--windows-skipped"],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(rejected.returncode, 2)

    def test_both_artifacts_match_original_markdown(self):
        self.assert_report(HEADER + WINDOWS_SECTION + LINUX_SECTION + NATIVE + FOOTER)

    def test_skipped_windows_leaves_linux_report(self):
        self.assert_report(
            HEADER
            + "## Windows unelevated\n\nWindows did not run.\n\n"
            + LINUX_SECTION
            + NATIVE
            + FOOTER,
            windows_skipped=True,
        )

    def test_native_section_is_optional(self):
        self.native.unlink()
        self.assert_report(HEADER + WINDOWS_SECTION + LINUX_SECTION + FOOTER)

    def test_coverage_midpoint_matches_dotnet_decimal_formatting(self):
        self.linux_coverage.write_text(
            '<coverage line-rate="0.92665" branch-rate="0.9068"/>', encoding="utf-8"
        )
        self.assert_report(
            HEADER
            + WINDOWS_SECTION
            + LINUX_SECTION.replace("92.76%", "92.67%")
            + NATIVE
            + FOOTER
        )

    def test_artifact_markers_must_be_unique_and_current(self):
        marker = self.windows_report / "commit.txt"
        for content in (None, "0" * 40, COMMIT):
            with self.subTest(content=content):
                if content is None:
                    marker.unlink()
                else:
                    marker.write_text(content, encoding="utf-8")
                if content == COMMIT:
                    (self.windows / "commit.txt").write_text(COMMIT, encoding="utf-8")
                self.assert_rejected("Windows artifact source commit mismatch")

    def test_missing_windows_coverage_is_not_a_skip(self):
        (self.windows / "coverage.xml").unlink()
        self.assert_rejected("coverage.xml")

    def test_artifact_linux_coverage_is_required_even_when_windows_is_skipped(self):
        self.linux_coverage.unlink()
        self.assert_rejected("coverage.cobertura.xml", windows_skipped=True)

    def test_invalid_artifact_contents_are_rejected(self):
        valid_results = test_results()
        for path, content, message in (
            (self.linux_results, "<TestRun/>", "TRX Counters"),
            (
                self.linux_results,
                valid_results.replace('total="12"', 'total="bad"'),
                "TRX counter",
            ),
            (
                self.linux_results,
                valid_results.replace('passed="9"', 'passed="8"'),
                "reconcile",
            ),
            (
                self.linux_results,
                valid_results.replace('failed="1"', 'failed="0"'),
                "reconcile",
            ),
            (
                self.linux_results,
                valid_results.replace('outcome="Failed"', 'outcome="Timeout"'),
                "outcome",
            ),
            (self.linux_coverage, "<broken", "unclosed token"),
            (self.linux_coverage, "<coverage/>", "Cobertura rate"),
            (
                self.linux_coverage,
                '<coverage line-rate="1.01" branch-rate="0"/>',
                "Cobertura rate",
            ),
            (
                self.linux_coverage,
                '<coverage line-rate="NaN" branch-rate="0"/>',
                "Cobertura rate",
            ),
            (self.native, "no line measurement", "native line coverage"),
            (
                self.native,
                "lines: 80% (8 out of 10)\nlines: 90% (9 out of 10)",
                "native line coverage",
            ),
        ):
            with self.subTest(content=content[:100]):
                original = path.read_bytes()
                path.write_text(content, encoding="utf-8")
                self.assert_rejected(message)
                path.write_bytes(original)

    def test_invalid_provenance_is_rejected(self):
        for option, value, message in (
            ("commit", "invalid", "source commit SHA"),
            ("run-id", "run-42", "workflow run id"),
            ("generated-at", "2026-10-24T14:34:56", "explicit offset"),
        ):
            with self.subTest(option=option):
                self.assert_rejected(message, **{option: value})


if __name__ == "__main__":
    unittest.main()
