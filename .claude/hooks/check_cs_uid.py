#!/usr/bin/env python3
"""PostToolUse hook: every .cs file needs a .cs.uid sidecar.

Deliberately narrow. The HK analyzers in tools/hike_analyzers own the C# code
conventions and run at build time; duplicating any of them here would give the
rules two sources of truth. This covers the one invariant the compiler cannot
see - the Godot UID sidecar, whose drift is a recurring, silent corruption.

Reads the hook payload on stdin, writes a note to stderr and exits 2 when a
sidecar is missing, which surfaces the message to Claude.
"""
import json
import os
import re
import sys

UID_LINE = re.compile(r"^uid://[a-z0-9]+$")


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    path = (payload.get("tool_input") or {}).get("file_path") or ""
    if not path.endswith(".cs") or path.endswith(".g.cs"):
        return 0

    # Only scripts/, addons/ and tools/ are imported by Godot and need sidecars.
    normalized = path.replace("\\", "/")
    if not any(("/%s/" % d) in normalized or normalized.startswith("%s/" % d)
               for d in ("scripts", "addons", "tools")):
        return 0
    sidecar = path + ".uid"
    if not os.path.exists(sidecar):
        sys.stderr.write(
            "%s has no .cs.uid sidecar. Every .cs under scripts/ addons/ tools/ needs "
            "one, and it must be MINTED, never hand-typed:\n"
            "    dotnet run --project tools/validate_uids -- --fix\n"
            % os.path.basename(path))
        return 2

    try:
        with open(sidecar, "r", encoding="utf-8") as handle:
            body = handle.read().strip()
    except OSError:
        return 0

    if not UID_LINE.match(body):
        sys.stderr.write(
            "%s is malformed (expected exactly one 'uid://...' line, found %r). "
            "Repair with: dotnet run --project tools/validate_uids -- --fix\n"
            % (os.path.basename(sidecar), body[:60]))
        return 2

    return 0


if __name__ == "__main__":
    sys.exit(main())
