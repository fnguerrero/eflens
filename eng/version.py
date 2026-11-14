"""Prints the package version after checking that EfLens.csproj and .mcp/server.json agree on it.

With an argument (a git tag such as v0.1.0), also checks that the tag matches. Used by CI and the release workflow.
"""

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]

csproj = (ROOT / "src" / "EfLens" / "EfLens.csproj").read_text(encoding="utf-8")
server = json.loads((ROOT / "src" / "EfLens" / ".mcp" / "server.json").read_text(encoding="utf-8"))

version = re.search(r"<Version>([^<]+)</Version>", csproj).group(1)
found = {
    "EfLens.csproj <Version>": version,
    "server.json version": server["version"],
    "server.json packages[0].version": server["packages"][0]["version"],
}
if len(sys.argv) > 1:
    found["tag"] = sys.argv[1].removeprefix("refs/tags/").removeprefix("v")

if len(set(found.values())) != 1:
    sys.exit("Version mismatch: " + ", ".join(f"{k} = {v}" for k, v in found.items()))

print(version)
