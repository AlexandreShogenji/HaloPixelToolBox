'use strict';
// Passive API tracing only. No device calls are made, and arguments/results are
// never changed. This captures application API traffic, not USB bus transfers.
const installed = new Set();
const pending = new Map();
const targetHandles = new Map();
let nextId = 0;

function stamp() { return { timestamp: new Date().toISOString(), timestamp_ms: Date.now() }; }
function metadata(event, extra) { send(Object.assign({ event, process_id: Process.id }, stamp(), extra || {})); }
function hex(bytes) { return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(' ').toUpperCase(); }
function report(buffer, length) {
  // Only exact 64-byte protocol packets, optionally preceded by a report ID.
  if (buffer.isNull() || (length !== 64 && length !== 65)) return null;
  try {
    const b = new Uint8Array(buffer.readByteArray(length));
    for (const offset of (length === 65 ? [1] : [0])) {
      const match = (b[offset] === 0x2e && b[offset + 1] === 0xaa && (b[offset + 2] === 0xec || b[offset + 2] === 0xed)) ||
        (b[offset] === 0x2f && b[offset + 1] === 0xbb && b[offset + 2] === 0xec);
      if (match) return { raw64: hex(b.slice(offset, offset + 64)), hex: hex(b), api_length: length, report_id: offset ? b[0] : null };
    }
  } catch (_) {}
  return null;
}
function count(pointer, fallback) { try { return pointer.isNull() ? fallback : pointer.readU32(); } catch (_) { return fallback; } }
function emit(api, direction, data, extra, at) {
  if (!data) return;
  send(Object.assign({ event: 'hid_report', capture_layer: 'api', process_id: Process.id, api, direction }, at || stamp(), data, extra || {}));
}
function hook(module, name, callbacks) {
  let address;
  try { address = module.findExportByName(name); } catch (_) { return; }
  if (!address || installed.has(address.toString())) return;
  try {
    Interceptor.attach(address, callbacks);
    installed.add(address.toString());
    metadata('hook_installed', { api: name, module: module.name, address: address.toString() });
  } catch (e) { metadata('hook_error', { api: name, error: String(e) }); }
}
function finishPending(overlapped, amount, api, success = true, error = 0) {
  const key = overlapped.toString();
  const entry = pending.get(key);
  if (!entry) return;
  // Incomplete/timeout is not terminal; retain the pending operation for retry.
  if (!success && (error === 996 || error === 258)) return;
  pending.delete(key);
  if (entry.direction === 'out') {
    metadata('write_completion', { api, direction: 'out', handle: entry.handle, operation_id: entry.id, write_api: entry.api, api_success: success, last_error: success ? null : error, transferred_length: amount, requested_length: entry.length, write_started: entry.started });
    return;
  }
  if (!success) return;
  emit(api, 'in', report(entry.buffer, amount), { handle: entry.handle, operation_id: entry.id, read_api: entry.api, completion: 'overlapped', requested_length: entry.length, read_started: entry.started });
}
function install(module) {
  const lower = module.name.toLowerCase();
  // Kernel32 may expose a distinct forwarding thunk that calls KernelBase.
  // Hooking both duplicates each call even with address deduplication.
  if (lower === 'kernelbase.dll') {
    hook(module, 'CreateFileW', {
      onEnter(args) {
        this.target = null;
        try {
          const path = args[0].readUtf16String().toUpperCase();
          if (!path.includes('VID_2D99&PID_A106')) return;
          const mi = path.match(/&MI_([0-9A-F]{2})/);
          const col = path.match(/&COL([0-9A-F]{2})/);
          // Keep only device class identifiers, never the path/serial number.
          this.target = { vid: '2D99', pid: 'A106', mi: mi ? mi[1] : null, collection: col ? col[1] : null, desired_access: '0x' + args[1].toUInt32().toString(16), share_flags: args[2].toUInt32() };
        } catch (_) {}
      },
      onLeave(ret) {
        if (!this.target) return;
        const success = ret.toInt32() !== -1;
        const handle = ret.toString();
        if (success) targetHandles.set(handle, this.target);
        metadata('device_open', Object.assign({ api: 'CreateFileW', handle, api_success: success, last_error: success ? null : this.lastError }, this.target));
      }
    });
    hook(module, 'WriteFile', {
      onEnter(args) {
        this.data = null;
        const length = args[2].toUInt32();
        if (length !== 64 && length !== 65) return;
        this.data = report(args[1], length);
        if (!this.data) return;
        this.handle = args[0].toString();
        if (!targetHandles.has(this.handle)) targetHandles.set(this.handle, { detected_by_protocol: true });
        this.at = stamp(); this.overlapped = !args[4].isNull(); this.overlappedPointer = args[4]; this.id = ++nextId;
        if (this.overlapped) pending.set(args[4].toString(), { direction: 'out', handle: this.handle, id: this.id, api: 'WriteFile', length, started: this.at.timestamp });
      },
      onLeave(ret) {
        if (!this.data) return;
        emit('WriteFile', 'out', this.data, { handle: this.handle, operation_id: this.id, api_success: !ret.isNull(), last_error: this.lastError, overlapped: this.overlapped, completion: !ret.isNull() ? 'returned_success' : (this.lastError === 997 ? 'pending' : 'failed') }, this.at);
        if (this.overlapped && (!ret.isNull() || this.lastError !== 997)) pending.delete(this.overlappedPointer.toString());
      }
    });
    hook(module, 'ReadFile', {
      onEnter(args) {
        this.length = args[2].toUInt32();
        if (this.length !== 64 && this.length !== 65) return;
        this.handle = args[0].toString(); this.buffer = args[1];
        this.bytes = args[3]; this.overlapped = args[4]; this.started = new Date().toISOString(); this.id = ++nextId;
        if (!this.overlapped.isNull()) pending.set(this.overlapped.toString(), { direction: 'in', buffer: this.buffer, length: this.length, handle: this.handle, id: this.id, api: 'ReadFile', started: this.started });
      },
      onLeave(ret) {
        if (this.length !== 64 && this.length !== 65) return;
        if (!ret.isNull()) {
          let n = count(this.bytes, 0);
          if (!n && !this.overlapped.isNull()) { try { n = this.overlapped.add(Process.pointerSize).readPointer().toUInt32(); } catch (_) {} }
          emit('ReadFile', 'in', report(this.buffer, n), { handle: this.handle, operation_id: this.id, completion: 'returned_success', requested_length: this.length, read_started: this.started });
          if (!this.overlapped.isNull()) pending.delete(this.overlapped.toString());
        } else if (this.lastError !== 997 && !this.overlapped.isNull()) pending.delete(this.overlapped.toString());
      }
    });
    for (const api of ['GetOverlappedResult', 'GetOverlappedResultEx']) hook(module, api, {
      onEnter(args) { this.overlapped = args[1]; this.bytes = args[2]; },
      onLeave(ret) { finishPending(this.overlapped, count(this.bytes, 0), api, !ret.isNull(), this.lastError); }
    });
    hook(module, 'GetQueuedCompletionStatus', {
      onEnter(args) { this.bytes = args[1]; this.overlappedOut = args[3]; },
      onLeave(ret) { try { finishPending(this.overlappedOut.readPointer(), count(this.bytes, 0), 'GetQueuedCompletionStatus', !ret.isNull(), this.lastError); } catch (_) {} }
    });
    hook(module, 'CloseHandle', {
      onEnter(args) { this.handle = args[0].toString(); },
      onLeave(ret) {
        if (targetHandles.has(this.handle)) metadata('device_close', { api: 'CloseHandle', handle: this.handle, api_success: !ret.isNull(), last_error: !ret.isNull() ? null : this.lastError });
        if (!ret.isNull()) {
          targetHandles.delete(this.handle);
          for (const [key, entry] of pending) if (entry.handle === this.handle) pending.delete(key);
        }
      }
    });
  }
  if (lower === 'hid.dll') {
    for (const api of ['HidD_SetOutputReport', 'HidD_SetFeature']) hook(module, api, {
      onEnter(args) { this.data = report(args[1], args[2].toUInt32()); this.handle = args[0].toString(); this.at = stamp(); },
      onLeave(ret) { emit(api, 'out', this.data, { handle: this.handle, api_success: !ret.isNull() }, this.at); }
    });
    for (const api of ['HidD_GetInputReport', 'HidD_GetFeature']) hook(module, api, {
      onEnter(args) { this.buffer = args[1]; this.length = args[2].toUInt32(); this.handle = args[0].toString(); },
      onLeave(ret) { if (!ret.isNull()) emit(api, 'in', report(this.buffer, this.length), { handle: this.handle, api_success: true }); }
    });
  }
}
Process.attachModuleObserver({ onAdded(module) { install(module); } });
metadata('trace_ready', { hooked_addresses: installed.size, protocol_headers: ['2E AA EC', '2E AA ED', '2F BB EC'], passive: true });
