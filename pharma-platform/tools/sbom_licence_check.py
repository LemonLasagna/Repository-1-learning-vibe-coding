#!/usr/bin/env python3
"""
Generates the software bill of materials (CycloneDX 1.5) for the API and the web client, then checks every component's licence
against the allow-list in tools/licence_policy.json.

  python3 tools/sbom_licence_check.py            write sbom/*.cdx.json and check licences (exit 1 on a licence not allowed)
  python3 tools/sbom_licence_check.py --check    check the committed sbom/*.cdx.json only (no tool runs)

Needs: npm (web) and the CycloneDX .NET tool `dotnet-CycloneDX` (api) on PATH for the write mode.
A licence that is not on the allow-list, or a component with no licence, fails the check. Add it to licence_policy.json with a
reason once someone has read the licence; never add it to make the check pass without that reading.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
POLICY = ROOT / "tools" / "licence_policy.json"
SBOM_DIR = ROOT / "sbom"
WEB_SBOM = SBOM_DIR / "web.cdx.json"
API_SBOM = SBOM_DIR / "api.cdx.json"
TOKEN = re.compile(r"\(|\)|\bAND\b|\bOR\b|\bWITH\b|[A-Za-z0-9.+\-]+")


def generate() -> None:
    SBOM_DIR.mkdir(exist_ok=True)
    npm = shutil.which("npm")
    dotnet_sbom = shutil.which("dotnet-CycloneDX") or str(Path.home() / ".dotnet" / "tools" / "dotnet-CycloneDX")
    if not npm or not Path(dotnet_sbom).exists():
        sys.exit("sbom: npm and dotnet-CycloneDX are needed to write the SBOMs (dotnet tool install --global CycloneDX).")
    web = subprocess.run([npm, "sbom", "--sbom-format", "cyclonedx", "--package-lock-only"], cwd=ROOT / "web",
                         capture_output=True, text=True, check=True)
    WEB_SBOM.write_text(web.stdout, encoding="utf-8")
    subprocess.run([dotnet_sbom, "PharmaOS.slnx", "-o", str(SBOM_DIR), "-fn", API_SBOM.name, "-F", "Json", "-spv", "1.5", "-t"],
                   cwd=ROOT, check=True, capture_output=True, text=True)


def licence_ids(component: dict) -> list[str]:
    ids = []
    for entry in component.get("licenses", []):
        if "expression" in entry:
            ids.append(entry["expression"])
        else:
            lic = entry.get("license", {})
            ids.append(lic.get("id") or lic.get("name") or "")
    return ids


def allowed_expression(expression: str, allowed: set[str], exceptions: set[str]) -> bool:
    """True when every licence named in the expression is allowed. OR-alternatives count as allowed when one of them is."""
    if not expression:
        return False
    if expression in allowed or expression in exceptions:
        return True
    ids = [t for t in TOKEN.findall(expression) if t not in ("(", ")", "AND", "OR", "WITH")]
    return bool(ids) and all(i in allowed or i in exceptions for i in ids)


def check(path: Path, policy: dict) -> list[str]:
    if not path.exists():
        return [f"{path.relative_to(ROOT)} is missing; run python3 tools/sbom_licence_check.py"]
    allowed = set(policy["allowed"])
    exceptions = {e["licence"] for e in policy.get("reviewed_exceptions", [])}
    problems = []
    for c in json.loads(path.read_text(encoding="utf-8")).get("components", []):
        name = f'{c.get("name")}@{c.get("version", "?")}'
        ids = licence_ids(c)
        if not ids:
            problems.append(f"{path.name}: {name} has no licence recorded")
            continue
        for expr in ids:
            if not allowed_expression(expr, allowed, exceptions):
                problems.append(f"{path.name}: {name} is licensed {expr!r}, which is not on the allow-list")
    return problems


def main() -> int:
    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    if "--check" not in sys.argv:
        generate()
    problems = check(WEB_SBOM, policy) + check(API_SBOM, policy)
    for p in problems:
        print(f"sbom: {p}", file=sys.stderr)
    if not problems:
        print(f"licence check passed: {WEB_SBOM.relative_to(ROOT)}, {API_SBOM.relative_to(ROOT)}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
