#!/usr/bin/env python3
"""Checks that every localization key used in an app exists in its strings.json, and lists unused keys.

Usage: python3 tools/check_strings.py src/TelloGameRoom.Dashboard [src/TelloGameRoom.Machine ...]
Exits with status 1 when a key is missing, so CI fails before a raw key can show up in the UI.
"""
import json
import re
import sys
from pathlib import Path

USED = [re.compile(r"\{l:Tr ([A-Za-z0-9_]+)\}"), re.compile(r'Loc\.T\("([A-Za-z0-9_]+)"'), re.compile(r'new NavItem\("([A-Za-z0-9_]+)"')]
# Keys built at runtime: "Err_" + code, "Perm_" + permission, "Cmd_" + command, "Action_" + audit action.
DYNAMIC_PREFIXES = ("Err_", "Perm_", "Cmd_", "Action_")


def check(app: Path) -> bool:
    strings = json.loads((app / "Resources" / "strings.json").read_text(encoding="utf-8"))
    used = set()
    for path in list(app.rglob("*.xaml")) + list(app.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        text = path.read_text(encoding="utf-8")
        for pattern in USED:
            used.update(k for k in pattern.findall(text) if not k.endswith("_") and k != "Key")
    missing = sorted(used - strings.keys())
    unused = sorted(k for k in strings.keys() - used if not k.startswith(DYNAMIC_PREFIXES))
    print(f"{app.name}: {len(used)} keys used, {len(strings)} defined")
    for key in missing:
        print(f"  MISSING {key}")
    for key in unused:
        print(f"  unused  {key}")
    return not missing


if __name__ == "__main__":
    results = [check(Path(p)) for p in sys.argv[1:]]
    sys.exit(0 if all(results) else 1)
