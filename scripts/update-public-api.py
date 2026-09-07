#!/usr/bin/env python3
"""Sync PublicAPI.Unshipped.txt for a project from the RS0016 (missing) / RS0017 (stale) diagnostics of a build.

Usage: python3 scripts/update-public-api.py src/QueryShape.Core/QueryShape.Core.csproj
"""
import re, subprocess, sys, pathlib

proj = pathlib.Path(sys.argv[1])
api = proj.parent / "PublicAPI.Unshipped.txt"
out = subprocess.run(["dotnet", "build", str(proj), "--no-incremental", "-p:TreatWarningsAsErrors=false", "--nologo", "-v", "q"], capture_output=True, text=True).stdout
missing = set(re.findall(r"RS0016: Symbol '(.+?)' is not part of the declared public API", out))
stale = set(re.findall(r"RS0017: Symbol '(.+?)' is part of the declared API, but is either not public or could not be found", out))
lines = [l.rstrip("\n") for l in api.read_text(encoding="utf-8-sig").splitlines()] if api.exists() else []
header = [l for l in lines if l.startswith("#")] or ["#nullable enable"]
body = [l for l in lines if l and not l.startswith("#") and l not in stale]
body = sorted(set(body) | missing)
api.write_text("\n".join(header + body) + "\n", encoding="utf-8")
print(f"{api}: +{len(missing)} -{len(stale)} -> {len(body)} symbols")
