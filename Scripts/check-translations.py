#!/usr/bin/env python3
# Validates mod translation files against the English source of truth and the
# mod's own Defs. Deterministic companion to the .claude/skills/translate flow:
# anything this script can prove is never re-derived by an agent.
#
# Checks per non-English language:
#   Keyed:       missing/extra keys, placeholder mismatches, stale EN comments
#   DefInjected: folder names resolvable as def types, defNames exist, field
#                paths structurally valid against def XML, stale EN comments,
#                uninjected label/description (warning)
#   All files:   well-formed XML, <LanguageData> root, UTF-8 no BOM, LF line
#                endings, no tabs, final newline (hygiene -> warnings)
#
# Staleness relies on the EN-comment convention: every translated entry carries
# the English source directly above it, e.g.
#   <!-- EN: Customize {0} -->
#   <UWU_CustomizeWeapon>...</UWU_CustomizeWeapon>
# A missing EN comment is a warning; an EN comment that no longer matches the
# current English text is an error (the translation is stale).
#
# Exit code: 1 if any errors (or, with --strict, any warnings), else 0.

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

PLACEHOLDER_RE = re.compile(r"\{[^{}]*\}")
EN_COMMENT_RE = re.compile(r"^\s*EN:\s?(.*)$", re.DOTALL)


def norm(text):
    return re.sub(r"\s+", " ", (text or "").strip())


def placeholders(text):
    return set(PLACEHOLDER_RE.findall(text or ""))


def parse_with_comments(path):
    # Returns (root, entries) where entries is [(key, text, en_comment)].
    # The EN comment for an entry is the nearest preceding EN: comment that
    # appears after the previous element (section headers are skipped).
    builder = ET.TreeBuilder(insert_comments=True)
    root = ET.parse(path, parser=ET.XMLParser(target=builder)).getroot()
    entries = []
    pending_en = None
    for node in root:
        if node.tag is ET.Comment:
            m = EN_COMMENT_RE.match(node.text or "")
            if m:
                pending_en = m.group(1)
        else:
            entries.append((node.tag, node, pending_en))
            pending_en = None
    return root, entries


def flatten_entry(elem):
    # A Keyed/DefInjected entry is either a single text value or a list of <li>.
    kids = list(elem)
    if kids:
        return [li.text or "" for li in kids]
    return elem.text or ""


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, path, msg):
        self.errors.append(f"{path}: {msg}")

    def warn(self, path, msg):
        self.warnings.append(f"{path}: {msg}")


def check_hygiene(path, report):
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        report.error(path, "UTF-8 BOM present")
    if b"\r" in raw:
        report.warn(path, "CRLF line endings (repo convention is LF)")
    if b"\t" in raw:
        report.warn(path, "tab indentation (repo convention is 2 spaces)")
    if raw and not raw.endswith(b"\n"):
        report.warn(path, "missing final newline")


def load_language_xml(path, report):
    check_hygiene(path, report)
    try:
        root, entries = parse_with_comments(path)
    except ET.ParseError as e:
        report.error(path, f"XML parse error: {e}")
        return None
    if root.tag != "LanguageData":
        report.error(path, f"root element is <{root.tag}>, expected <LanguageData>")
        return None
    return entries


def collect_keyed(lang_dir, report):
    # key -> (text, en_comment, path)
    keyed = {}
    for path in sorted((lang_dir / "Keyed").glob("**/*.xml")) if (lang_dir / "Keyed").is_dir() else []:
        entries = load_language_xml(path, report)
        for key, elem, en in entries or []:
            if key in keyed:
                report.error(path, f"duplicate key <{key}> (also in {keyed[key][2].name})")
            keyed[key] = (flatten_entry(elem), en, path)
    return keyed


def collect_defs(defs_dirs):
    # tag -> {defName -> element}; abstract parents kept under their Name attr.
    defs = {}
    parents = {}
    for defs_dir in defs_dirs:
        for path in sorted(defs_dir.glob("**/*.xml")):
            try:
                root = ET.parse(path).getroot()
            except ET.ParseError:
                continue
            if root.tag != "Defs":
                continue
            for elem in root:
                if elem.tag is ET.Comment:
                    continue
                name = elem.get("Name")
                if name is not None:
                    parents.setdefault(elem.tag, {})[name] = elem
                def_name = elem.findtext("defName")
                if def_name:
                    defs.setdefault(elem.tag, {})[def_name] = elem
    return defs, parents


def resolve_field(elem, segments, parents):
    # Structurally walk a DefInjected path (field names, li indices) through a
    # def element, following ParentName inheritance. Returns the matched
    # element, or None. A path may legitimately stop at a list field
    # (full-list translation), which is a match on the list element itself.
    if not segments:
        return elem
    head, rest = segments[0], segments[1:]
    if head.isdigit():
        kids = [k for k in elem if k.tag == "li"]
        idx = int(head)
        if idx < len(kids):
            return resolve_field(kids[idx], rest, parents)
        return None
    child = elem.find(head)
    if child is not None:
        return resolve_field(child, rest, parents)
    parent_name = elem.get("ParentName")
    pool = parents.get(elem.tag, {})
    while parent_name and parent_name in pool:
        parent = pool[parent_name]
        child = parent.find(head)
        if child is not None:
            return resolve_field(child, rest, parents)
        parent_name = parent.get("ParentName")
    return None


def check_language(lang_dir, english_keyed, defs, parents, report):
    lang = lang_dir.name

    # --- Keyed ---
    keyed = collect_keyed(lang_dir, report)
    label = f"[{lang}/Keyed]"
    for key in sorted(set(english_keyed) - set(keyed)):
        report.error(label, f"missing key <{key}>")
    for key, (_, _, path) in sorted(keyed.items()):
        if key not in english_keyed:
            report.error(path, f"unknown key <{key}> (not in English)")
            continue
        text, en, _ = keyed[key]
        en_text = english_keyed[key][0]
        if isinstance(text, str) and isinstance(en_text, str):
            if placeholders(text) != placeholders(en_text):
                report.error(path, f"<{key}> placeholders {sorted(placeholders(text))} "
                                   f"!= English {sorted(placeholders(en_text))}")
        if en is None:
            report.warn(path, f"<{key}> has no EN: comment")
        elif isinstance(en_text, str) and norm(en) != norm(en_text):
            report.error(path, f"<{key}> is STALE: EN comment does not match current "
                               f"English text")

    # --- DefInjected ---
    inj_root = lang_dir / "DefInjected"
    if not inj_root.is_dir():
        return
    for folder in sorted(p for p in inj_root.iterdir() if p.is_dir()):
        def_type = folder.name
        if def_type not in defs:
            report.error(folder, f"folder does not match any def type in this mod's "
                                 f"Defs (expected one of: {', '.join(sorted(defs))})")
            continue
        injected_paths = set()
        for path in sorted(folder.glob("**/*.xml")):
            entries = load_language_xml(path, report)
            for key, elem, en in entries or []:
                segments = key.split(".")
                def_name = segments[0]
                if def_name not in defs[def_type]:
                    report.error(path, f"<{key}>: no {def_type} named {def_name}")
                    continue
                injected_paths.add((def_name, segments[1] if len(segments) > 1 else ""))
                target = resolve_field(defs[def_type][def_name], segments[1:], parents)
                if target is None:
                    report.error(path, f"<{key}>: field path does not exist on the def")
                    continue
                text = flatten_entry(elem)
                en_text = target.text if not list(target) else None
                if isinstance(text, str) and en_text is not None:
                    if placeholders(text) != placeholders(en_text):
                        report.error(path, f"<{key}> placeholders {sorted(placeholders(text))} "
                                           f"!= English {sorted(placeholders(en_text))}")
                if en is None:
                    report.warn(path, f"<{key}> has no EN: comment")
                elif en_text is not None and norm(en) != norm(en_text):
                    report.error(path, f"<{key}> is STALE: EN comment does not match "
                                       f"current def XML")
        for def_name, elem in sorted(defs[def_type].items()):
            for field in ("label", "description"):
                if elem.find(field) is not None and (def_name, field) not in injected_paths:
                    report.warn(f"[{lang}/DefInjected/{def_type}]",
                                f"{def_name}.{field} exists but is not translated")


def main():
    ap = argparse.ArgumentParser(description="Validate mod translation files.")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                    help="repo root (default: parent of Scripts/)")
    ap.add_argument("--strict", action="store_true", help="treat warnings as errors")
    args = ap.parse_args()

    lang_roots = sorted(args.root.glob("*/Languages")) + \
                 ([args.root / "Languages"] if (args.root / "Languages").is_dir() else [])
    defs_dirs = sorted(args.root.glob("*/Defs")) + \
                ([args.root / "Defs"] if (args.root / "Defs").is_dir() else [])
    if not lang_roots:
        print(f"No Languages/ directory found under {args.root}", file=sys.stderr)
        return 2

    report = Report()
    english_keyed = {}
    languages = []
    for lang_root in lang_roots:
        for lang_dir in sorted(p for p in lang_root.iterdir() if p.is_dir()):
            if lang_dir.name == "English":
                english_keyed.update(collect_keyed(lang_dir, report))
            else:
                languages.append(lang_dir)

    if not english_keyed:
        print("No English Keyed strings found; nothing to check against.", file=sys.stderr)
        return 2

    defs, parents = collect_defs(defs_dirs)
    for lang_dir in languages:
        check_language(lang_dir, english_keyed, defs, parents, report)

    for line in report.errors:
        print(f"ERROR   {line}")
    for line in report.warnings:
        print(f"WARNING {line}")
    checked = ", ".join(sorted({l.name for l in languages})) or "none"
    print(f"\n{len(english_keyed)} English keys; languages checked: {checked}")
    print(f"{len(report.errors)} error(s), {len(report.warnings)} warning(s)")
    return 1 if report.errors or (args.strict and report.warnings) else 0


if __name__ == "__main__":
    sys.exit(main())
