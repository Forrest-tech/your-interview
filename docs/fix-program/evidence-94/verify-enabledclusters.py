#!/usr/bin/env python3
"""Ticket 94: replicate Services.Gateway/Program.cs lines 43-83 EnabledClusters filtering.

Mirrors the .NET configuration layering exactly as the running gateway sees it:
  1. appsettings.json ReverseProxy.Clusters (destinations) + HealthChecks.EnabledClusters
  2. Environment overrides (env beats JSON) for the four destinations that differ
     in this sandbox run: jobs->5125 (repo appsettings still says stale 5263),
     interviews->26164, knowledge->26165, assessment->26166 (my own instances).
  3. Filter rule (Program.cs): non-empty EnabledClusters (comma-split, trimmed,
     case-insensitive) => probe only listed clusters; else probe all.
  4. Probe URL = destination address (trimmed trailing '/') + '/health/live'.
"""
import json

APPS = "/home/hatch/workspace/wt-t94/src/Services.Gateway/appsettings.json"
OVERRIDES = {
    "jobs": "http://127.0.0.1:5125/",
    "interviews": "http://127.0.0.1:26164/",
    "knowledge": "http://127.0.0.1:26165/",
    "assessment": "http://127.0.0.1:26166/",
}

cfg = json.load(open(APPS))["ReverseProxy"]
clusters = cfg["Clusters"]
enabled_raw = cfg["HealthChecks"]["EnabledClusters"]
enabled = {n.strip().lower() for n in enabled_raw.split(",") if n.strip()}
print("EnabledClusters raw:", enabled_raw)
print("Enabled set:", sorted(enabled))

probed = {}
for name, cluster in clusters.items():
    if enabled and name.lower() not in enabled:
        print(f"  EXCLUDED by filter: {name}")
        continue
    addr = OVERRIDES.get(name, list(cluster["Destinations"].values())[0]["Address"])
    probed[name] = addr.rstrip("/") + "/health/live"

print(f"\nProbe list ({len(probed)} clusters):")
for name, url in sorted(probed.items()):
    print(f"  {name}: {url}")

expected = {"identity", "jobs", "interviews", "knowledge", "assessment", "analytics", "aigateway"}
assert set(probed) == expected, f"cluster set mismatch: {set(probed)} != {expected}"
assert not any("5300" in u or "ocr" in n for n, u in probed.items()), "OCR leaked into probe list!"
assert len(probed) == 7
print("\nASSERTIONS PASS: exactly the 7 business clusters, OCR excluded, no 5300.")
