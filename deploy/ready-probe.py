#!/usr/bin/env python3
"""Answer whether kgsm-reactor has reported itself ready since a given moment.

    ready-probe.py <journal-dir> <since-epoch-seconds>     → exit 0 when ready

Used by deploy/deploy-common.sh's health_probe. It exists as a file rather than a heredoc so the
check can be read, run and debugged on its own — a deploy that fails its health gate is exactly
when somebody wants to run the probe by hand.

The reactor serves no socket and no port, so `leaf_ready` in its own journal is its health signal.
The `since` bound is what makes the check mean anything: without it a line from the previous run
satisfies the probe instantly, and the gate passes on a service that never came up.
"""
import glob
import json
import os
import sys
from datetime import datetime, timezone


def main(directory: str, since: float) -> int:
    segments = sorted(glob.glob(os.path.join(directory, "*.ndjson")))
    if not segments:
        return 1

    # Only the newest segment. A leaf_ready older than this deploy proves nothing, and reading the
    # whole history to find one would get slower every day for no added confidence.
    try:
        lines = open(segments[-1], encoding="utf-8").read().splitlines()
    except OSError:
        return 1

    for line in reversed(lines):
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if event.get("EventType") != "leaf_ready":
            continue
        stamp = str(event.get("Timestamp", "")).replace("Z", "+00:00")
        try:
            when = datetime.fromisoformat(stamp)
        except ValueError:
            return 1
        if when.tzinfo is None:
            when = when.replace(tzinfo=timezone.utc)
        return 0 if when.timestamp() >= since else 1
    return 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: ready-probe.py <journal-dir> <since-epoch-seconds>")
    sys.exit(main(sys.argv[1], float(sys.argv[2])))
