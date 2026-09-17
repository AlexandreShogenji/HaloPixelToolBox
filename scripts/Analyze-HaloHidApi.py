#!/usr/bin/env python3
"""Analyze passive Halo HID API JSONL captures without accessing any device.

Supports Capture-HaloHidApi.py events and the older Frida {type, payload} wrapper
whose reports contain payload.time/dir/hex. Standard library only. Example:
  python scripts/Analyze-HaloHidApi.py artifacts/capture.jsonl \
      --markers artifacts/markers.jsonl --output-prefix artifacts/capture-decoded

Writes JSONL, CSV, TXT and summary JSON. Reports are preserved, including repeated
buffers, failed calls and pending calls. API observations are not USB bus frames.
Checksum results state whether a formula matches each sample; they do not assume
that a rule valid for host packets must also be valid for all device replies.
"""
from __future__ import annotations

import argparse
import bisect
from collections import Counter
import csv
from datetime import datetime
import json
from pathlib import Path
import runpy
import sys


# Reuse the byte layout, timestamp handling and marker reader, without invoking
# the pcap CLI or copying its transport-specific parsing into the API decoder.
_shared = runpy.run_path(str(Path(__file__).with_name("Analyze-HaloHidCapture.py")))
decode_halo = _shared["decode_halo"]
hex_bytes = _shared["hex_bytes"]
local_time = _shared["local_time"]
parse_marker_time = _shared["parse_marker_time"]
read_markers = _shared["read_markers"]
LOCAL_TZ = _shared["LOCAL_TZ"]
HEADERS = _shared["HEADERS"]
CHECKSUM_FORMULA = "sum(report[1:6+payload_length]) & 0xFF"


def timestamp_of(event: dict) -> tuple[int, str]:
    for key in ("timestamp_ns", "timestamp", "time", "timestamp_ms", "host_receive_utc"):
        if event.get(key) is not None:
            return parse_marker_time(event[key], key), key
    raise ValueError("report has no capture timestamp")


def api_observation(event: dict, legacy: bool) -> str:
    direction = str(event.get("direction", event.get("dir", ""))).upper()
    if legacy:
        return "observed_entry_only" if direction == "OUT" else "read_completion_observed"
    completion = event.get("completion")
    if completion is not None:
        return str(completion)
    if event.get("api_success") is True:
        return "returned_success"
    if event.get("api_success") is False:
        return "returned_failure"
    return "result_unknown"


def decode_api_report(event: dict, source: str, line: int, legacy: bool,
                      markers: list[dict], marker_times: list[int]) -> dict:
    raw = event.get("raw64", event.get("hex"))
    if not isinstance(raw, str):
        raise ValueError("report has no hexadecimal string")
    data = bytes.fromhex(raw)
    report_id = event.get("report_id")
    if len(data) == 65 and data[1:4] in HEADERS:
        report_id, data = data[0], data[1:]
    if len(data) != 64:
        raise ValueError(f"expected a 64-byte report, got {len(data)} bytes")
    timestamp_ns, timestamp_source = timestamp_of(event)
    direction = str(event.get("direction", event.get("dir", "UNKNOWN"))).upper()
    marker_index = bisect.bisect_right(marker_times, timestamp_ns) - 1
    marker = markers[marker_index] if marker_index >= 0 else None
    row = {"source_file": source, "source_line": line, "capture_layer": "api",
           "schema": "legacy_frida_wrapper" if legacy else "capture_halo_hid_api",
           "timestamp_ns": timestamp_ns, "time_local": local_time(timestamp_ns),
           "timestamp_source": timestamp_source, "host_receive_utc": event.get("host_receive_utc"),
           "segment_index": marker_index, "action": marker["action"] if marker else "before_first_marker",
           "marker_phase": marker.get("phase") if marker else None,
           "ms_since_marker": round((timestamp_ns - marker["timestamp_ns"]) / 1_000_000, 3) if marker else None,
           "process_id": event.get("process_id"), "api": event.get("api"), "direction": direction,
           "handle": event.get("handle"), "operation_id": event.get("operation_id"),
           "api_observation": api_observation(event, legacy), "api_success": event.get("api_success"),
           "last_error": event.get("last_error"), "overlapped": event.get("overlapped"),
           "completion": event.get("completion"), "read_started": event.get("read_started"),
           "requested_length": event.get("requested_length"),
           "api_length": event.get("api_length", len(bytes.fromhex(event.get("hex", raw)))),
           "report_id": report_id, "report_length": 64, "report_hex": hex_bytes(data)}
    row.update(decode_halo(data))
    # EC is observed in official app writes; "legacy" is not an inferred app age.
    if data[:3] == b"\x2e\xaa\xec":
        row["protocol"] = "host_request_ec"
    matches = row["checksum_candidate_matches"]
    row["checksum_formula_tested"] = CHECKSUM_FORMULA
    row["checksum_status"] = ("sample_matches_sum_from_byte1" if matches is True else
                              "sample_mismatches_sum_from_byte1" if matches is False else
                              "not_testable_" + row["checksum_status"])
    row["header_direction_consistent"] = (direction == "IN" if data[:3] == b"\x2f\xbb\xec"
                                          else direction == "OUT" if data[:3] in HEADERS else None)
    row["matching_command_out_source_line"] = None
    row["matching_command_out_delta_ms"] = None
    row["matching_command_out_payload_equal"] = None
    row["same_command_out_previous_delta_ms"] = None
    row["same_command_out_previous_payload_equal"] = None
    return row


def clock_candidate(row: dict) -> dict | None:
    """Expose the observed 0x77 calendar hypothesis without naming unknown bytes."""
    if row["direction"] != "OUT" or row["command_hex"] != "0x77" or row["payload_length"] != 9:
        return None
    payload = bytes.fromhex(row["payload_hex"])
    result = {"source_file": row["source_file"], "source_line": row["source_line"],
              "capture_time_local": row["time_local"], "payload_hex": row["payload_hex"],
              "year_big_endian": int.from_bytes(payload[:2], "big"), "month": payload[2],
              "day": payload[3], "hour": payload[4], "minute": payload[5], "second": payload[6],
              "trailing_two_bytes_hex": hex_bytes(payload[7:]), "calendar_candidate": None,
              "host_minus_calendar_ms_assuming_utc_plus_08": None}
    try:
        candidate = datetime(result["year_big_endian"], payload[2], payload[3], payload[4], payload[5], payload[6], tzinfo=LOCAL_TZ)
        candidate_ns = parse_marker_time(candidate.isoformat(), "timestamp")
        result["calendar_candidate"] = candidate.isoformat()
        result["host_minus_calendar_ms_assuming_utc_plus_08"] = round((row["timestamp_ns"] - candidate_ns) / 1_000_000, 3)
    except ValueError:
        pass
    return result


def analyze(paths: list[Path], markers: list[dict], response_window_ms: float = 2000) -> tuple[list[dict], dict]:
    rows, warnings, capture_files = [], [], []
    marker_times = [item["timestamp_ns"] for item in markers]
    metadata_counts = Counter()
    for path in paths:
        file_info = {"source_file": str(path), "lines": 0, "report_count": 0,
                     "capture_start_seen": False, "capture_end_seen": False,
                     "declared_report_count": None, "script_error_count": 0}
        with path.open(encoding="utf-8-sig") as handle:
            for line, text in enumerate(handle, 1):
                file_info["lines"] = line
                if not text.strip():
                    continue
                item = json.loads(text)
                if not isinstance(item, dict):
                    warnings.append(f"{path.name}:{line}: expected a JSON object")
                    continue
                legacy = item.get("type") == "send" and isinstance(item.get("payload"), dict)
                event = item["payload"] if legacy else item
                is_report = event.get("event") == "hid_report" or (legacy and "hex" in event and "dir" in event)
                if not is_report:
                    name = str(event.get("event", item.get("type", "unknown_metadata")))
                    metadata_counts[name] += 1
                    if name == "capture_start":
                        file_info["capture_start_seen"] = True
                    if name == "capture_end":
                        file_info["capture_end_seen"] = True
                        file_info["declared_report_count"] = event.get("report_count")
                    if name in ("script_error", "hook_error", "error"):
                        file_info["script_error_count"] += 1
                    continue
                try:
                    row = decode_api_report(event, str(path), line, legacy, markers, marker_times)
                except (TypeError, ValueError, OverflowError) as error:
                    warnings.append(f"{path.name}:{line}: {error}")
                    continue
                rows.append(row)
                file_info["report_count"] += 1
                if row["header_direction_consistent"] is False:
                    warnings.append(f"{path.name}:{line}: API direction conflicts with recognized protocol header")
        if file_info["capture_start_seen"] and not file_info["capture_end_seen"]:
            warnings.append(f"{path.name}: capture_end missing; capture may be running or interrupted")
        if file_info["declared_report_count"] is not None and file_info["declared_report_count"] != file_info["report_count"]:
            warnings.append(f"{path.name}: capture_end report_count differs from decoded count")
        capture_files.append(file_info)

    rows.sort(key=lambda row: row["timestamp_ns"])
    commands, payloads, segments, observations, checksums, apis = (Counter() for _ in range(6))
    previous_out = {}
    for row in rows:
        command_key = (row["direction"], row["header_hex"], row["command_hex"])
        commands[command_key] += 1
        payloads[(*command_key, row["payload_hex"])] += 1
        segments[(row["segment_index"], row["action"], row["marker_phase"], *command_key)] += 1
        observations[(row["direction"], row["api_observation"])] += 1
        checksums[(row["direction"], row["header_hex"], row["checksum_status"])] += 1
        apis[(row["direction"], str(row["api"]))] += 1
        # Group by file/process/handle so unrelated processes cannot be paired.
        scope = (row["source_file"], row["process_id"], row["handle"], row["command_hex"])
        previous = previous_out.get(scope)
        if previous is not None:
            delta_ms = (row["timestamp_ns"] - previous["timestamp_ns"]) / 1_000_000
            equal = row["payload_hex"] == previous["payload_hex"]
            if row["direction"] == "OUT":
                row["same_command_out_previous_delta_ms"] = round(delta_ms, 3)
                row["same_command_out_previous_payload_equal"] = equal
            elif row["direction"] == "IN" and delta_ms <= response_window_ms:
                row["matching_command_out_source_line"] = previous["source_line"]
                row["matching_command_out_delta_ms"] = round(delta_ms, 3)
                row["matching_command_out_payload_equal"] = equal
        if row["direction"] == "OUT":
            previous_out[scope] = row

    def counts(counter: Counter, names: tuple[str, ...]) -> list[dict]:
        return [dict(zip(names, key), count=value) for key, value in sorted(counter.items(), key=lambda item: str(item[0]))]

    summary = {"capture_layer": "api", "timezone": "+08:00", "files": capture_files,
               "reports": len(rows), "first_report_time": rows[0]["time_local"] if rows else None,
               "last_report_time": rows[-1]["time_local"] if rows else None,
               "warnings": warnings, "warning_count": len(warnings), "markers": markers,
               "metadata_event_counts": dict(metadata_counts),
               "command_counts": counts(commands, ("direction", "header_hex", "command_hex")),
               "payload_counts": counts(payloads, ("direction", "header_hex", "command_hex", "payload_hex")),
               "segment_command_counts": counts(segments, ("segment_index", "action", "marker_phase", "direction", "header_hex", "command_hex")),
               "api_observation_counts": counts(observations, ("direction", "api_observation")),
               "api_counts": counts(apis, ("direction", "api")),
               "checksum_formula_tested": CHECKSUM_FORMULA,
               "checksum_counts": counts(checksums, ("direction", "header_hex", "checksum_status")),
               "checksum_sample_matches": sum(row["checksum_candidate_matches"] is True for row in rows),
               "checksum_sample_mismatches": sum(row["checksum_candidate_matches"] is False for row in rows),
               "checksum_not_testable": sum(row["checksum_candidate_matches"] is None for row in rows),
               "clock_calendar_candidates": [candidate for row in rows if (candidate := clock_candidate(row)) is not None],
               "response_candidate_window_ms": response_window_ms,
               "notes": [
                   "API buffers are not USB bus frames; no physical-delivery or device-effect guarantee is inferred.",
                   "Legacy OUT observations occur at WriteFile entry and contain no API return status.",
                   "Repeated reports are preserved; counts do not silently deduplicate matching buffers.",
                   "Marker segments use the most recent marker only; no causal relationship is assumed.",
                   "A nearby IN with the same command is a response candidate, not a proven acknowledgment.",
                   "Checksum match means this exact sample satisfies the formula, not a universal protocol guarantee.",
                   "0x77 calendar decoding is a field hypothesis; final two bytes remain uninterpreted.",
                   "Clock deltas assume local UTC+08:00 for comparison; packet timezone semantics are not inferred.",
                   "Reports may contain device identifiers or text; review before publishing."]}
    return rows, summary


def write_outputs(prefix: Path, rows: list[dict], summary: dict) -> dict[str, str]:
    paths = {suffix: Path(str(prefix) + suffix) for suffix in (".jsonl", ".csv", ".txt", ".summary.json")}
    prefix.parent.mkdir(parents=True, exist_ok=True)
    with paths[".jsonl"].open("w", encoding="utf-8", newline="\n") as jsonl, \
         paths[".csv"].open("w", encoding="utf-8-sig", newline="") as csv_handle, \
         paths[".txt"].open("w", encoding="utf-8", newline="\n") as text_handle:
        writer = csv.DictWriter(csv_handle, fieldnames=list(rows[0])) if rows else None
        if writer:
            writer.writeheader()
        for row in rows:
            jsonl.write(json.dumps(row, ensure_ascii=False) + "\n")
            writer.writerow(row)
            text_handle.write(f"{row['time_local']} {Path(row['source_file']).name}:{row['source_line']} "
                              f"{row['direction']} api={row['api']} result={row['api_observation']} "
                              f"cmd={row['command_hex']} len={row['payload_length']} "
                              f"checksum={row['checksum_status']} action={row['action']} "
                              f"phase={row['marker_phase']} marker_age_ms={row['ms_since_marker']}\n"
                              f"  {row['report_hex']}\n")
    paths[".summary.json"].write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {key: str(value) for key, value in paths.items()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("captures", nargs="+", type=Path, help="one or more API capture JSONL files")
    parser.add_argument("--markers", type=Path, help="timestamped action markers JSONL")
    parser.add_argument("--output-prefix", "-o", type=Path, help="default: first capture stem + -decoded")
    parser.add_argument("--response-window-ms", type=float, default=2000, help="window for nearby same-command IN candidates")
    args = parser.parse_args()
    if args.response_window_ms < 0:
        parser.error("--response-window-ms must be nonnegative")
    prefix = args.output_prefix or args.captures[0].with_name(args.captures[0].stem + "-decoded")
    inputs = {path.resolve() for path in args.captures}
    if args.markers:
        inputs.add(args.markers.resolve())
    if any(Path(str(prefix) + suffix).resolve() in inputs for suffix in (".jsonl", ".csv", ".txt", ".summary.json")):
        parser.error("An output path would overwrite an input file")
    try:
        rows, summary = analyze(args.captures, read_markers(args.markers), args.response_window_ms)
        outputs = write_outputs(prefix, rows, summary)
        print(json.dumps({"reports": summary["reports"], "checksum_sample_matches": summary["checksum_sample_matches"],
                          "checksum_sample_mismatches": summary["checksum_sample_mismatches"],
                          "checksum_not_testable": summary["checksum_not_testable"],
                          "warnings": summary["warning_count"], "outputs": outputs}, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, TypeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
