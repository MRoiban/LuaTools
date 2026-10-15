#!/usr/bin/env python3
"""Validate the localization RESX files.

Requires only English Strings.resx and French Strings.fr.resx:
  - rejects missing French or additional locale files
  - is well-formed XML
  - has EXACTLY the same set of <data> keys (no missing, no extra)
  - every value keeps the same set of {0}/{1}... placeholders as English (catches broken interpolation)

Exit code 1 if any problem is found. Run locally or in CI.
Usage: python scripts/check-i18n.py
"""
import re
import sys
import glob
import os
import xml.etree.ElementTree as ET

RES_DIR = os.path.join("src", "LuaToolsGui", "Resources")
ENGLISH = os.path.join(RES_DIR, "Strings.resx")

PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')


def parse(path):
    """Return {key: value} for a RESX file (also raises if XML is malformed)."""
    text = open(path, encoding="utf-8").read()
    root = ET.fromstring(text)
    values = {}
    for entry in root.findall("data"):
        key = entry.attrib["name"]
        if key in values:
            raise ValueError(f"{path}: duplicate key {key}")
        value = entry.findtext("value", default="")
        if not value.strip():
            raise ValueError(f"{path}: empty translation for {key}")
        values[key] = value
    return values


def placeholders(value):
    return set(PLACEHOLDER_RE.findall(value))


def main():
    if not os.path.exists(ENGLISH):
        print(f"ERROR: {ENGLISH} not found (run from repo root)")
        return 1

    try:
        en = parse(ENGLISH)
    except (ET.ParseError, ValueError) as e:
        print(f"ERROR: invalid English resource: {e}")
        return 1
    base_keys = set(en)
    problems = []

    paths = sorted(glob.glob(os.path.join(RES_DIR, "Strings.*.resx")))
    expected = os.path.join(RES_DIR, "Strings.fr.resx")
    if expected not in paths:
        problems.append("Strings.fr.resx: required French translation is missing")
    for path in paths:
        if path != expected:
            problems.append(f"{os.path.basename(path)}: unsupported locale (only en + fr are allowed)")
            continue
        name = os.path.basename(path)
        try:
            tr = parse(path)
        except (ET.ParseError, ValueError) as e:
            problems.append(f"{name}: INVALID RESOURCE. {e}")
            continue

        missing = base_keys - set(tr)
        extra = set(tr) - base_keys
        if missing:
            problems.append(f"{name}: missing {len(missing)} key(s): {', '.join(sorted(missing)[:10])}")
        if extra:
            problems.append(f"{name}: {len(extra)} unknown key(s): {', '.join(sorted(extra)[:10])}")

        # Placeholder parity (only for keys present in both).
        for k in base_keys & set(tr):
            if placeholders(en[k]) != placeholders(tr[k]):
                problems.append(
                    f"{name}: key '{k}' placeholder mismatch "
                    f"(en={sorted(placeholders(en[k]))} vs {sorted(placeholders(tr[k]))})")

    count = len(glob.glob(os.path.join(RES_DIR, "Strings.*.resx")))
    if problems:
        print(f"i18n check FAILED ({len(problems)} problem(s) across {count} language files):")
        for p in problems:
            print("  -", p)
        return 1

    print(f"i18n check OK: English + French only, {len(base_keys)} keys each, "
          "XML valid, placeholders consistent.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
