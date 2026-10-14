"""Prints one version's section of docs/changelog.md.

    python scripts/changelog.py 0.3.0           the section's bullets as Markdown, for the GitHub release notes
    python scripts/changelog.py 0.3.0 --nexus   one plain-text line per bullet, for Nexus Mods changelog entries

A leading "v" on the version is ignored. A version with no section prints nothing and exits 0, so a workflow can
fall back to its own text; the reason goes to standard error.
"""
import re
import sys
from pathlib import Path

CHANGELOG = Path(__file__).resolve().parent.parent / "docs" / "changelog.md"


def bullets(version: str) -> list[str]:
    items: list[str] = []
    inside = False
    for line in CHANGELOG.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            inside = line[3:].strip() == version
            continue
        if not inside:
            continue
        if line.startswith("- "):
            items.append(line[2:].strip())
        elif line.startswith("  ") and line.strip() and items:
            items[-1] += " " + line.strip()
    return items


def plain(text: str) -> str:
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)   # [text](url) -> text
    text = re.sub(r"\*\*([^*]+)\*\*", r"\1", text)          # **bold** -> bold
    return text.replace("`", "")


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    version = args[0].removeprefix("v")
    items = bullets(version)
    if not items:
        print(f"No section for {version} in {CHANGELOG}", file=sys.stderr)
        return 0
    nexus = "--nexus" in sys.argv
    for item in items:
        print(plain(item) if nexus else "- " + item)
    return 0


if __name__ == "__main__":
    sys.exit(main())
