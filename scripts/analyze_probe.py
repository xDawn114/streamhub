"""Read native probe windows without mixing startup and live-stream encoders.

Times are local log timestamp intervals, not avcodec_open2 instrumentation.
Missing endpoints remain None. This script never starts an engine or client.
"""
import argparse
import datetime as dt
import json
import re
from pathlib import Path

STAMP = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\]")
BOUNDARY = re.compile(r"\b(begin|end) stage=(startup-encoder-probe|launch-encoder-probe)\b")
CREATE = re.compile(r"Creating encoder \[([^\]]+)\]")


def analyze(text):
    probes = []
    current = None
    pending = None
    nvenc_start = None
    for line in text.splitlines():
        match = STAMP.search(line)
        when = dt.datetime.fromisoformat(match[1]) if match else None
        boundary = BOUNDARY.search(line)
        if boundary and boundary[1] == "begin":
            current = dict(stage=boundary[2], complete=False, elapsedMs=None,
                           encoders=[], nvencAttemptMs=None, cacheReused=False)
            probes.append(current)
            pending = nvenc_start = None
        if current is None:
            continue
        if boundary and boundary[1] == "end" and boundary[2] == current["stage"]:
            elapsed = re.search(r"elapsed_ms=(\d+)", line)
            current.update(complete=True, elapsedMs=int(elapsed[1]) if elapsed else None)
            current = pending = nvenc_start = None
            continue
        if "encoder-probe=reused" in line:
            current["cacheReused"] = True
        if "Trying encoder [nvenc]" in line:
            nvenc_start = when
        if nvenc_start and when and ("not supported on this GPU" in line or "Trying encoder [quicksync]" in line):
            current["nvencAttemptMs"] = round((when - nvenc_start).total_seconds() * 1000)
            nvenc_start = None
        created = CREATE.search(line)
        if created:
            # A failed creation cannot borrow a later codec's bitrate endpoint.
            pending = dict(codec=created[1], creationWindowMs=None)
            current["encoders"].append(pending)
            pending = (pending, when)
        if pending and "Streaming bitrate is" in line:
            row, start = pending
            if start and when and when >= start:
                row["creationWindowMs"] = round((when - start).total_seconds() * 1000)
            pending = None
    return dict(probes=probes, scope="begin/end probe windows only; live encoder initialization excluded",
                timestampsAreAvcodecInstrumentation=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = [dict(file=str(path), **analyze(path.read_text(encoding="utf-8-sig", errors="replace"))) for path in args.logs]
    content = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(content, encoding="utf-8", newline="\n")
    else:
        print(content)


if __name__ == "__main__":
    main()
