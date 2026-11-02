#!/usr/bin/env python3
"""Lists the failed and warned checks of a DASH-IF conformance run (the JSON the validator prints), with their messages.
usage: dashif-summary.py <output-file>"""
import json, sys

text = open(sys.argv[1]).read()
data = json.JSONDecoder().raw_decode(text[text.index("{"):])[0]
counts = {}


def walk(node, path):
    if isinstance(node, dict):
        leaf = "state" in node and "checks" not in node
        if leaf:
            state = node["state"]
            counts[state] = counts.get(state, 0) + 1
            if state not in ("PASS", "NOT_RUN", "INFO"):
                msgs = [m for m in node.get("messages", []) if "Used resource" not in m]
                print(state, "/".join(path), "|", "; ".join(msgs)[:300])
        for k, v in node.items():
            if isinstance(v, (dict, list)):
                walk(v, path + [k])
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, path + [str(i)])


walk(data, [])
print("states:", counts)
