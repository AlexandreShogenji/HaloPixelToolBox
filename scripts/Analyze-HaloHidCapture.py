#!/usr/bin/env python3
"""Decode a *completed* USBPcap classic pcap without opening a USB device.

Python standard library only. Example:
  python scripts/Analyze-HaloHidCapture.py capture.pcap --device 9 \
      --markers markers.jsonl --output-prefix capture-decoded

Creates .jsonl, .csv, .txt and .summary.json files. All offsets are zero based.
Only actual captured 64-byte reports on interrupt/control transfers are emitted;
65-byte transfers with one report-ID byte before a known Halo header are supported.
USB addresses identify the capture session, not a persistent device identity.
Interrupt interface numbers cannot be inferred without interface descriptors.

USBPcap header reference: https://desowin.org/usbpcap/captureformat.html
Outbound Halo layout/checksum: HaloPixelToolBox.Core/Utilities/HidPacketBuilder.cs.
The inbound checksum is deliberately a candidate, never declared validated merely
because the outbound summation happens to match it.
"""

from __future__ import annotations

import argparse
import bisect
import csv
import json
import struct
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path


LOCAL_TZ = timezone(timedelta(hours=8))
USB_HEADER = struct.Struct("<HQIHBHHBBI")
TRANSFER_NAMES = {0: "isochronous", 1: "interrupt", 2: "control", 3: "bulk"}
STAGE_NAMES = {0: "setup", 1: "data", 2: "status", 3: "complete"}
HEADERS = {b"\x2e\xaa\xed": "host_request_ed",
           b"\x2e\xaa\xec": "legacy_host_ec",
           b"\x2f\xbb\xec": "device_response_ec"}


def hex_bytes(value: bytes) -> str:
    return value.hex(" ").upper()


def local_time(timestamp_ns: int) -> str:
    seconds, fraction_ns = divmod(timestamp_ns, 1_000_000_000)
    value = datetime.fromtimestamp(seconds, LOCAL_TZ)
    value = value.replace(microsecond=fraction_ns // 1000)
    return value.isoformat(timespec="milliseconds")


def parse_marker_time(value, key: str) -> int:
    if isinstance(value, (int, float)):
        multiplier = {"timestamp_ns": 1, "epoch_ns": 1,
                      "timestamp_ms": 1_000_000, "epoch_ms": 1_000_000}.get(key)
        if multiplier is None:
            # Numeric Unix timestamps: seconds by default, milliseconds above 1e11.
            multiplier = 1_000_000 if abs(value) >= 100_000_000_000 else 1_000_000_000
        return round(value * multiplier)
    value = str(value).strip().replace("Z", "+00:00")
    parsed = datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=LOCAL_TZ)
    delta = parsed.astimezone(timezone.utc) - datetime(1970, 1, 1, tzinfo=timezone.utc)
    return (delta.days * 86400 + delta.seconds) * 1_000_000_000 + delta.microseconds * 1000


def read_markers(path: Path | None) -> list[dict]:
    if path is None:
        return []
    result = []
    keys = ("timestamp_ns", "epoch_ns", "timestamp_ms", "epoch_ms", "timestamp",
            "time", "time_local", "local_time", "at", "ts")
    with path.open(encoding="utf-8-sig") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            item = json.loads(line)
            key = next((key for key in keys if item.get(key) is not None), None)
            if key is None:
                raise ValueError(f"Marker line {line_no} has no recognized timestamp")
            timestamp_ns = parse_marker_time(item[key], key)
            action = item.get("action", item.get("name", item.get("label", item.get("event", f"marker_{line_no}"))))
            result.append({"timestamp_ns": timestamp_ns, "time_local": local_time(timestamp_ns),
                           "action": str(action), "phase": item.get("phase"), "source_line": line_no})
    return sorted(result, key=lambda item: item["timestamp_ns"])


def pcap_packets(path: Path):
    """Yield (frame, timestamp_ns, original_length, data) preserving pcap numbering."""
    magic_formats = {b"\xd4\xc3\xb2\xa1": ("<", 1000), b"\xa1\xb2\xc3\xd4": (">", 1000),
                     b"\x4d\x3c\xb2\xa1": ("<", 1), b"\xa1\xb2\x3c\x4d": (">", 1)}
    with path.open("rb") as handle:
        global_header = handle.read(24)
        if len(global_header) < 24:
            raise ValueError("Missing or incomplete classic pcap global header")
        if global_header[:4] == b"\x0a\x0d\x0d\x0a":
            raise ValueError("pcapng is unsupported; convert with editcap -F pcap first")
        if global_header[:4] not in magic_formats:
            raise ValueError("Unsupported pcap magic")
        endian, fraction_scale = magic_formats[global_header[:4]]
        major, minor, _zone, _sigfigs, snaplen, network = struct.unpack(endian + "HHiIII", global_header[4:])
        if (major, minor) != (2, 4):
            raise ValueError(f"Unsupported pcap version {major}.{minor}")
        if network & 0xFFFF != 249:
            raise ValueError(f"Expected LINKTYPE_USBPCAP (249), got {network & 0xFFFF}")
        frame = 0
        while packet_header := handle.read(16):
            frame += 1
            if len(packet_header) != 16:
                raise ValueError(f"Truncated pcap record header at frame {frame}; stop capture first")
            seconds, fraction, captured_length, original_length = struct.unpack(endian + "IIII", packet_header)
            if captured_length > snaplen or captured_length > 64 * 1024 * 1024:
                raise ValueError(f"Invalid captured length {captured_length} at frame {frame}")
            if fraction * fraction_scale >= 1_000_000_000:
                raise ValueError(f"Invalid fractional timestamp at frame {frame}")
            data = handle.read(captured_length)
            if len(data) != captured_length:
                raise ValueError(f"Truncated pcap record data at frame {frame}; stop capture first")
            yield frame, seconds * 1_000_000_000 + fraction * fraction_scale, original_length, data


def decode_halo(report: bytes) -> dict:
    header = report[:3]
    result = {"protocol": HEADERS.get(header, "unrecognized"), "header_hex": hex_bytes(header),
              "command_hex": None, "payload_length": None, "payload_hex": None,
              "checksum_offset": None, "checksum_observed_hex": None,
              "checksum_candidate_hex": None, "checksum_candidate_matches": None,
              "checksum_status": "unrecognized", "padding_nonzero": None}
    if header not in HEADERS:
        return result
    result["command_hex"] = f"0x{report[3]:02X}"
    payload_length = int.from_bytes(report[4:6], "big")
    result["payload_length"] = payload_length
    checksum_offset = 6 + payload_length
    result["checksum_offset"] = checksum_offset
    if checksum_offset >= len(report):
        result["checksum_status"] = "length_out_of_bounds"
        return result
    result["payload_hex"] = hex_bytes(report[6:checksum_offset])
    observed = report[checksum_offset]
    candidate = sum(report[1:checksum_offset]) & 0xFF
    result.update(checksum_observed_hex=f"0x{observed:02X}",
                  checksum_candidate_hex=f"0x{candidate:02X}",
                  checksum_candidate_matches=observed == candidate,
                  padding_nonzero=any(report[checksum_offset + 1:]))
    if header == b"\x2f\xbb\xec":
        result["checksum_status"] = "inbound_rule_unverified"
    elif report[3] == 0xE8:
        # BuildText in this repository uses a different legacy text checksum/layout.
        result["checksum_status"] = "text_rule_unverified"
    else:
        result["checksum_status"] = "valid_host_sum" if observed == candidate else "invalid_host_sum"
    return result


def analyze(capture: Path, markers: list[dict], device: int | None, bus: int | None,
            endpoints: list[int] | None, consume) -> dict:
    summary = {"format": "LINKTYPE_USBPCAP classic pcap", "timezone": "+08:00",
               "frames": 0, "selected_usb_frames": 0, "reports": 0,
               "first_frame_time": None, "last_frame_time": None,
               "first_report_time": None, "last_report_time": None,
               "filters": {"device": device, "bus": bus, "endpoints": endpoints},
               "warnings": [], "warning_count": 0, "markers": markers,
               "notes": ["Only captured data is emitted; zero-data URB submissions/completions are omitted.",
                         "Marker segments run from a marker up to the next marker, without proving causality.",
                         "Interrupt interface is unknown unless independently mapped from descriptors.",
                         "Inbound checksum uses an unverified candidate sum from byte 1; matches are not proof.",
                         "Full reports may contain device identifiers; review before publishing."]}
    commands, segments, ignored, checksums = Counter(), Counter(), Counter(), Counter()
    setup_by_irp = {}
    marker_times = [item["timestamp_ns"] for item in markers]

    def warn(frame, message):
        summary["warning_count"] += 1
        if len(summary["warnings"]) < 100:
            summary["warnings"].append(f"frame {frame}: {message}")

    for frame, timestamp_ns, original_length, data in pcap_packets(capture):
        summary["frames"] += 1
        timestamp = local_time(timestamp_ns)
        if summary["first_frame_time"] is None:
            summary["first_frame_time"] = timestamp
        summary["last_frame_time"] = timestamp
        if len(data) < USB_HEADER.size:
            warn(frame, "USBPcap header is shorter than 27 bytes")
            continue
        header_len, irp, status, function, info, usb_bus, usb_device, endpoint, transfer, data_len = USB_HEADER.unpack_from(data)
        if device is not None and usb_device != device or bus is not None and usb_bus != bus:
            continue
        if endpoints is not None and endpoint not in endpoints:
            continue
        summary["selected_usb_frames"] += 1
        if header_len < USB_HEADER.size or header_len > len(data):
            warn(frame, f"invalid USBPcap header length {header_len}")
            continue
        if original_length > len(data):
            warn(frame, "snaplen truncated this USBPcap frame")
        captured_data = data[header_len:header_len + data_len]
        if len(captured_data) < data_len:
            warn(frame, f"only {len(captured_data)} of {data_len} USB data bytes captured")
            continue
        if len(data) > header_len + data_len:
            warn(frame, f"{len(data) - header_len - data_len} bytes follow declared USB payload")
        phase = "completion" if info & 1 else "submission"
        direction = "IN" if endpoint & 0x80 else "OUT"
        setup, stage = None, None
        irp_key = (usb_bus, usb_device, irp)
        if transfer == 2:
            if header_len < 28:
                warn(frame, "control transfer lacks a stage byte")
                continue
            stage = data[27]
            if stage == 0:
                if len(captured_data) < 8:
                    warn(frame, "control setup is shorter than 8 bytes")
                    continue
                bm_request, request, value, index, length = struct.unpack("<BBHHH", captured_data[:8])
                setup = {"bmRequestType": f"0x{bm_request:02X}", "bRequest": f"0x{request:02X}",
                         "wValue": f"0x{value:04X}", "wIndex": index, "wLength": length,
                         "direction": "IN" if bm_request & 0x80 else "OUT",
                         "is_hid_report": bm_request in (0x21, 0xA1) and request in (1, 9)}
                setup_by_irp[irp_key] = setup
                captured_data = captured_data[8:]
            else:
                setup = setup_by_irp.get(irp_key)
            if setup is not None:
                direction = setup["direction"]
            if stage in (2, 3):
                setup_by_irp.pop(irp_key, None)
        if not captured_data:
            continue
        if transfer not in (1, 2):
            ignored[f"{TRANSFER_NAMES.get(transfer, str(transfer))}:{len(captured_data)}"] += 1
            continue
        report_id = None
        report = captured_data
        if len(report) == 65 and report[1:4] in HEADERS:
            report_id, report = report[0], report[1:]
        if len(report) != 64:
            ignored[f"{TRANSFER_NAMES[transfer]}:{len(captured_data)}"] += 1
            continue
        if transfer == 2 and setup is not None and not setup["is_hid_report"] and report[:3] not in HEADERS:
            ignored[f"control_non_hid:{len(captured_data)}"] += 1
            continue
        marker_index = bisect.bisect_right(marker_times, timestamp_ns) - 1
        marker = markers[marker_index] if marker_index >= 0 else None
        row = {"frame": frame, "timestamp_ns": timestamp_ns, "time_local": timestamp,
               "segment_index": marker_index, "action": marker["action"] if marker else "before_first_marker",
               "marker_phase": marker.get("phase") if marker else None,
               "ms_since_marker": round((timestamp_ns - marker["timestamp_ns"]) / 1_000_000, 3) if marker else None,
               "usb_bus": usb_bus, "usb_device": usb_device, "endpoint_hex": f"0x{endpoint:02X}",
               "direction": direction, "transfer": TRANSFER_NAMES[transfer], "urb_phase": phase,
               "irp_id": f"0x{irp:016X}", "urb_status": f"0x{status:08X}",
               "urb_function": f"0x{function:04X}", "control_stage": STAGE_NAMES.get(stage),
               "control_setup": setup, "usb_data_length": data_len, "report_id": report_id,
               "report_length": len(report), "report_hex": hex_bytes(report)}
        row.update(decode_halo(report))
        consume(row)
        summary["reports"] += 1
        if summary["first_report_time"] is None:
            summary["first_report_time"] = timestamp
        summary["last_report_time"] = timestamp
        command_key = f"{direction} {row['header_hex']} {row['command_hex']}"
        commands[command_key] += 1
        segments[(marker_index, row["action"], command_key)] += 1
        checksums[row["checksum_status"]] += 1
    summary["command_counts"] = dict(sorted(commands.items()))
    summary["checksum_counts"] = dict(sorted(checksums.items()))
    summary["ignored_data_lengths"] = dict(sorted(ignored.items()))
    summary["segment_command_counts"] = [
        {"segment_index": key[0], "action": key[1], "command": key[2], "count": value}
        for key, value in sorted(segments.items())]
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture", type=Path, help="completed classic .pcap with USBPcap link type 249")
    parser.add_argument("--markers", type=Path, help="JSONL containing ISO/Unix time and action/name/label")
    parser.add_argument("--output-prefix", "-o", type=Path, help="default: capture stem + -decoded")
    parser.add_argument("--device", type=lambda s: int(s, 0), help="session USB device address")
    parser.add_argument("--bus", type=lambda s: int(s, 0), help="USBPcap bus number")
    parser.add_argument("--endpoint", action="append", type=lambda s: int(s, 0), help="optional, repeatable, e.g. 0x04 or 0x84")
    args = parser.parse_args()
    prefix = args.output_prefix or args.capture.with_name(args.capture.stem + "-decoded")
    paths = {suffix: Path(str(prefix) + suffix) for suffix in (".jsonl", ".csv", ".txt", ".summary.json")}
    input_paths = {args.capture.resolve()}
    if args.markers:
        input_paths.add(args.markers.resolve())
    if any(path.resolve() in input_paths for path in paths.values()):
        parser.error("An output path would overwrite an input file")
    try:
        markers = read_markers(args.markers)
        prefix.parent.mkdir(parents=True, exist_ok=True)
        with paths[".jsonl"].open("w", encoding="utf-8", newline="\n") as jsonl, \
             paths[".csv"].open("w", encoding="utf-8-sig", newline="") as csv_handle, \
             paths[".txt"].open("w", encoding="utf-8", newline="\n") as text_handle:
            writer = None

            def consume(row):
                nonlocal writer
                jsonl.write(json.dumps(row, ensure_ascii=False) + "\n")
                if writer is None:
                    writer = csv.DictWriter(csv_handle, fieldnames=list(row))
                    writer.writeheader()
                csv_row = {key: json.dumps(value, ensure_ascii=False) if isinstance(value, (dict, list)) else value
                           for key, value in row.items()}
                writer.writerow(csv_row)
                text_handle.write(f"{row['time_local']} frame={row['frame']} {row['direction']} "
                                  f"bus={row['usb_bus']} device={row['usb_device']} ep={row['endpoint_hex']} "
                                  f"cmd={row['command_hex']} len={row['payload_length']} "
                                  f"checksum={row['checksum_status']} action={row['action']}\n"
                                  f"  {row['report_hex']}\n")

            summary = analyze(args.capture, markers, args.device, args.bus, args.endpoint, consume)
        paths[".summary.json"].write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"frames": summary["frames"], "reports": summary["reports"],
                          "warnings": summary["warning_count"], "outputs": {key: str(value) for key, value in paths.items()}},
                         ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, struct.error) as error:
        print(f"error: {error}\nOutputs may be partial; do not treat them as a completed analysis.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
