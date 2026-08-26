#!/usr/bin/env python3
"""Verifies the vendored pipeline snapshot is container-safe.

The R&D tree the pipeline is developed in lives under a developer's home directory, and
earlier generations of these scripts hardcoded that path for the work root, the input
images and the ONNX model. The current entrypoint takes all of those as arguments, so
the vendored copy should contain no absolute developer paths at all - and the sidecar
would fail confusingly at runtime rather than at vendor time if one crept back in.

This runs as the last step of vendor.sh and fails the vendor rather than shipping a
snapshot that only works on one laptop. Run it standalone to re-check an existing
snapshot. Idempotent; it never edits anything.
"""
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
PIPELINE = HERE / "pipeline"
ENTRYPOINT = PIPELINE / "wall_pipeline.py"

# Absolute paths into somebody's home, and the expanduser() call that builds one.
FORBIDDEN = re.compile(
    r"/Users/[^\s'\"]+|/home/(?!runner\b)[^\s'\"]+|expanduser\s*\(|~/Desktop")
# Doc comments legitimately name the R&D directories they were vendored from; only
# executable lines are a problem, so blank out strings and comments before scanning.
COMMENT = re.compile(r"#.*$", re.M)
DOCSTRING = re.compile(r"('''|\"\"\")(?:.|\n)*?\1")


def offenders(text: str):
    stripped = COMMENT.sub("", DOCSTRING.sub("", text))
    return [m.group(0) for m in FORBIDDEN.finditer(stripped)]


def main() -> int:
    if not ENTRYPOINT.exists():
        print(f"nothing to check: {ENTRYPOINT} is missing; run vendor.sh", file=sys.stderr)
        return 1

    bad = []
    files = sorted(PIPELINE.rglob("*.py"))
    for path in files:
        for hit in offenders(path.read_text(encoding="utf-8")):
            bad.append(f"{path.relative_to(HERE)}: {hit}")

    if bad:
        print("vendored pipeline still carries developer paths:", file=sys.stderr)
        for line in bad:
            print(f"  {line}", file=sys.stderr)
        return 2

    print(f"vendored pipeline checked: {len(files)} file(s), no developer paths")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
