#!/usr/bin/env python3
"""Summarize cumulative probe counters by marked workload phase."""
import csv
import json
import sys


def stable_hash(value):
    first = second = 5381
    for index, character in enumerate(value):
        if index % 2:
            second = ((second << 5) + second) ^ ord(character)
        else:
            first = ((first << 5) + first) ^ ord(character)
    result = (first + second * 1566083941) & 0xffffffff
    return str(result if result < 0x80000000 else result - 0x100000000)


def summarize(path):
    phases = {}
    header = None
    with open(path, newline="") as source:
        for values in csv.reader(source):
            if values[0] == "utc":
                header = values
                continue
            row = dict(zip(header, values))
            phases.setdefault(row["phase"], []).append(row)
    prior = {}
    result = []
    payload_hash = stable_hash("SstProbePayload")
    for phase, rows in phases.items():
        latest = {row["method"]: row for row in rows}
        if phase.endswith("_start"):
            before = prior.get(payload_hash, {})
            after = latest.get(payload_hash, {})
            item = {"phase": phase, "from": rows[0]["utc"], "to": rows[-1]["utc"]}
            for key in ("calls", "inspectionAllocated", "inspectionTicks", "packages", "packagePayloadBytes"):
                item[key] = int(after.get(key, 0)) - int(before.get(key, 0))
            for key in ("received", "invalid", "runtimeAllocated", "gc0", "gc1", "gc2"):
                item[key + "Delta"] = int(rows[-1][key]) - int(rows[0][key])
            for key in ("managedBytes", "rssBytes"):
                item[key + "Start"] = int(rows[0][key])
                item[key + "End"] = int(rows[-1][key])
            result.append(item)
        prior.update(latest)
    return result


if __name__ == "__main__":
    for filename in sys.argv[1:]:
        print(json.dumps({"file": filename, "phases": summarize(filename)}, indent=2))
