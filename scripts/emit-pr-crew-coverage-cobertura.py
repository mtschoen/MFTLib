#!/usr/bin/env python3
"""Emit `.pr-crew-coverage.json` from one or more Cobertura coverage XML files.

Vendored helper for .NET / coverlet repos. coverlet emits Cobertura XML
(`CoverletOutputFormat=cobertura`); point this at the XML(s) it produced and
it writes the schema pr-crew's coverage gate reads from each repo's canonical
clone. Stdlib only.

Usage:
    python scripts/emit-pr-crew-coverage-cobertura.py [GLOB_OR_PATH ...]

With no arguments, globs `**/coverage.cobertura.xml` under the current
directory (excluding bin/ and obj/). Always writes `./.pr-crew-coverage.json`.

Multiple XMLs (e.g. one per test project) are merged at the line level: a
source line counts as covered if any report shows hits > 0, so overlapping
reports don't double-count.

Schema (see pr-crew's coverage-gate spec for details):
    {
      "percent_covered": <float 0..100>,
      "as_of":           "<ISO-8601 UTC timestamp>",
      "by_file":         { "<source path>": <float 0..100>, ... }
    }
"""

import glob
import json
import sys
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timezone
from pathlib import Path


def _resolve_xml_paths(patterns: list[str]) -> list[Path]:
    paths: list[Path] = []
    for pattern in patterns:
        candidate = Path(pattern)
        if candidate.is_file():
            paths.append(candidate)
            continue
        paths.extend(Path(match) for match in glob.glob(pattern, recursive=True))
    # coverlet leaves stale copies under build output dirs; ignore them.
    return [p for p in paths if "/bin/" not in str(p) and "/obj/" not in str(p)]


def _collect_lines(xml_paths: list[Path]) -> dict[tuple[str, str], bool]:
    """Map (filename, line_number) -> covered, merged across all reports."""
    lines: dict[tuple[str, str], bool] = {}
    for path in xml_paths:
        root = ElementTree.parse(path).getroot()
        for class_node in root.iter("class"):
            filename = class_node.get("filename", "")
            for line_node in class_node.iter("line"):
                key = (filename, line_node.get("number", ""))
                hits = int(line_node.get("hits", "0"))
                lines[key] = lines.get(key, False) or hits > 0
    return lines


def main(argv: list[str]) -> int:
    patterns = argv[1:] or ["**/coverage.cobertura.xml"]
    xml_paths = _resolve_xml_paths(patterns)
    if not xml_paths:
        print(f"error: no Cobertura XML matched {patterns}; run the test suite "
              "with coverlet (CoverletOutputFormat=cobertura) first", file=sys.stderr)
        return 1

    lines = _collect_lines(xml_paths)
    total = len(lines)
    if total == 0:
        print("error: Cobertura XML contained no source lines", file=sys.stderr)
        return 1
    covered = sum(1 for is_covered in lines.values() if is_covered)

    per_file_total: dict[str, int] = {}
    per_file_covered: dict[str, int] = {}
    for (filename, _number), is_covered in lines.items():
        per_file_total[filename] = per_file_total.get(filename, 0) + 1
        per_file_covered[filename] = per_file_covered.get(filename, 0) + int(is_covered)

    payload = {
        "percent_covered": round(100.0 * covered / total, 2),
        "as_of": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "by_file": {
            filename: round(100.0 * per_file_covered[filename] / per_file_total[filename], 2)
            for filename in sorted(per_file_total)
        },
    }
    out = Path(".pr-crew-coverage.json")
    out.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out} ({payload['percent_covered']}% across {len(payload['by_file'])} "
          f"files, merged from {len(xml_paths)} cobertura report(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
