#!/usr/bin/env python3
"""Generates Strings.resx and Strings.es.resx from a strings.json file ({"Key": ["English", "Español"]}).

Usage: python3 tools/resx.py src/TelloGameRoom.Dashboard/Resources
Editing strings.json and re-running this is easier and safer than hand-editing two XML files.
"""
import json
import sys
from pathlib import Path
from xml.sax.saxutils import escape

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def write(path: Path, strings: dict, index: int) -> None:
    lines = [HEADER]
    for key in sorted(strings):
        lines.append(f'  <data name="{escape(key)}" xml:space="preserve"><value>{escape(strings[key][index])}</value></data>\n')
    lines.append("</root>\n")
    path.write_text("".join(lines), encoding="utf-8")


def main() -> None:
    folder = Path(sys.argv[1])
    strings = json.loads((folder / "strings.json").read_text(encoding="utf-8"))
    missing = [k for k, v in strings.items() if len(v) != 2 or not all(v)]
    if missing:
        sys.exit(f"Missing translation for: {', '.join(missing)}")
    write(folder / "Strings.resx", strings, 0)
    write(folder / "Strings.es.resx", strings, 1)
    print(f"{len(strings)} strings written to {folder}")


if __name__ == "__main__":
    main()
