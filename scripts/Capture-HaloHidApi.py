#!/usr/bin/env python3
"""Passively capture Halo HID application API buffers with Frida.

Requires frida. Never sends commands to the device. --spawn instruments before
process resume; default cwd is the executable directory. Captures only matching
64-byte packets (or a 65-byte report with one report ID). Other buffers never
leave the target. API observations are not proof of USB delivery. A failed or
pending WriteFile is explicitly labelled. Ctrl-C detaches without killing apps.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import signal
import sys
import threading
import time


def now():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--attach", type=int, metavar="PID")
    target.add_argument("--spawn", type=Path, metavar="EXE")
    parser.add_argument("--cwd", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=float, default=60)
    parser.add_argument("--script", type=Path, default=Path(__file__).with_name("halo_hid_trace.js"))
    args = parser.parse_args()
    if args.seconds <= 0:
        parser.error("--seconds must be positive")
    try:
        import frida
    except ImportError:
        parser.error("Install frida in the Python environment first: python -m pip install frida")

    source = args.script.read_text(encoding="utf-8")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation prevents accidental overwrite of evidence.
    stream = args.output.open("x", encoding="utf-8", buffering=1)
    lock = threading.Lock()
    done = threading.Event()
    errors = []
    reports = [0]
    device = frida.get_local_device()
    session = None
    spawned_pid = None
    resumed = False

    def record(data):
        data["host_receive_utc"] = now()
        with lock:
            stream.write(json.dumps(data, ensure_ascii=False) + "\n")
            stream.flush()

    def on_message(message, binary):
        if message["type"] == "send":
            payload = message["payload"]
            record(payload)
            if payload.get("event") == "hid_report":
                reports[0] += 1
            elif payload.get("event") == "trace_ready":
                print(f"TRACE_READY pid={pid} output={args.output.resolve()}", flush=True)
        elif message["type"] == "error":
            errors.append(message)
            record({"event": "script_error", "message": message})
            print(message.get("stack", str(message)), file=sys.stderr, flush=True)
            done.set()

    def on_detached(reason, crash=None):
        record({"event": "detached", "reason": reason})
        done.set()

    def stop(*unused):
        done.set()

    signal.signal(signal.SIGINT, stop)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, stop)
    try:
        if args.spawn:
            exe = args.spawn.resolve(strict=True)
            cwd = (args.cwd or exe.parent).resolve(strict=True)
            pid = device.spawn([str(exe)], cwd=str(cwd), stdio="inherit")
            spawned_pid = pid
        else:
            pid = args.attach
        record({"event": "capture_start", "process_id": pid, "mode": "spawn" if args.spawn else "attach", "executable": str(args.spawn) if args.spawn else None, "seconds": args.seconds, "capture_layer": "api", "passive": True})
        session = device.attach(pid)
        session.on("detached", on_detached)
        script = session.create_script(source)
        script.on("message", on_message)
        script.load()
        if spawned_pid:
            device.resume(pid)
            resumed = True
        deadline = time.monotonic() + args.seconds
        while not done.is_set() and time.monotonic() < deadline:
            done.wait(min(0.5, max(0, deadline - time.monotonic())))
    finally:
        if spawned_pid and not resumed:
            # Never leave a newly spawned app suspended after an instrumentation error.
            try:
                device.resume(spawned_pid)
            except Exception:
                pass
        if session is not None:
            try:
                session.detach()
            except Exception:
                pass
        record({"event": "capture_end", "report_count": reports[0], "script_error_count": len(errors)})
        stream.close()
    print(f"Captured {reports[0]} matching API reports -> {args.output.resolve()}", flush=True)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
