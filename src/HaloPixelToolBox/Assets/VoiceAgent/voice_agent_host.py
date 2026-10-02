"""Local wake-word and command-capture worker for Halo PixelBar.

The parent process communicates with this worker through newline-delimited JSON:

* stdout: state, command, and error events only
* stdin:  {"action":"resume"}, {"action":"stop"}, or
          {"action":"notify","id":"...","cue":"task_completed","listen_after":"wake"}
* notification events: type=notification, id, cue, stage=started/played/failed

Human-readable diagnostics are written to stderr so stdout remains machine-safe.
"""

from __future__ import annotations

import argparse
import array
import collections
import contextlib
import ctypes
import json
import math
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time
import wave
from typing import Any, Callable


os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("MODELSCOPE_OFFLINE", "1")
os.environ.setdefault("PYTHONUTF8", "1")

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)

PROTOCOL_OUT = sys.stdout
PROTOCOL_LOCK = threading.Lock()
SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_MODEL = Path.home() / ".cache/modelscope/hub/models/iic/SenseVoiceSmall"
DEFAULT_VAD_MODEL = (
    Path.home()
    / ".cache/modelscope/hub/models/iic/speech_fsmn_vad_zh-cn-16k-common-pytorch"
)
DEFAULT_REPLY = SCRIPT_DIR / "reply-xiaoxiao-loud.wav"
DEFAULT_PROCESSING_REPLY = SCRIPT_DIR / "reply-received-xiaoxiao.wav"
DEFAULT_INPUT_DEVICE = "麦克风 (花再 Halo PixelBar)"
NOTIFICATION_CUES = {
    "task_started": "progress", "task_progress": "progress",
    "approval_required": "waiting", "input_required": "waiting",
    "task_completed": "completed", "task_failed": "failed", "task_cancelled": "cancelled",
}

PRIMARY_WAKE_ALIASES = (
    "花再花再",
    "花在花在",
    "花再花在",
    "花在花再",
)

WAKE_ALIASES = PRIMARY_WAKE_ALIASES + (
    "花仔花仔",
    "花彩花彩",
    "华仔华仔",
    "华再华再",
    "华在华在",
    "好在好在",
    "好在好仔",
)

# Match only whole wake-shaped utterances, including repeated calls and short
# fillers. Do not accept common phrases such as “好的好的” or our own “我在”.
_WAKE_PHRASE = r"(?:(?:[花华][再在仔彩]){2}|好在好在|好在好仔)"
_WAKE_FILLER = r"[啊呀哎诶喂]{0,2}"
WAKE_PATTERN = re.compile(
    rf"{_WAKE_FILLER}{_WAKE_PHRASE}"
    rf"(?:{_WAKE_FILLER}{_WAKE_PHRASE}){{0,2}}{_WAKE_FILLER}"
)


def emit(event_type: str, **payload: Any) -> None:
    event = {"type": event_type, **payload}
    with PROTOCOL_LOCK:
        PROTOCOL_OUT.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")
        PROTOCOL_OUT.flush()


def emit_state(state: str, message: str, **payload: Any) -> None:
    emit("state", state=state, message=message, **payload)


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def clean_recognition_text(raw: str) -> str:
    text = re.sub(r"<\|.*?\|>", "", raw)
    return re.sub(r"\s+", " ", text).strip()


def normalize_wake_text(value: str) -> str:
    value = clean_recognition_text(value).lower().replace("華", "华")
    return re.sub(r"[^\u3400-\u9fff0-9a-z]", "", value)


def matches_wake_word(value: str) -> bool:
    return WAKE_PATTERN.fullmatch(normalize_wake_text(value)) is not None


class ActionChannel:
    def __init__(self) -> None:
        self.actions: queue.Queue[str] = queue.Queue()
        self.notifications: queue.Queue[dict[str, str]] = queue.Queue(maxsize=16)
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self._read, name="voice-agent-stdin", daemon=True)

    def start(self) -> None:
        self.thread.start()

    def _read(self) -> None:
        try:
            for line in sys.stdin:
                try:
                    request = json.loads(line)
                    self._accept(request)
                except (json.JSONDecodeError, AttributeError, TypeError) as exc:
                    log(f"Ignoring invalid stdin request: {exc}")
                    continue

                if self.stopped():
                    return
        finally:
            # A closed parent pipe means the worker is no longer owned.
            self.stop_event.set()
            self.actions.put("stop")

    def stopped(self) -> bool:
        return self.stop_event.is_set()

    def _accept(self, request: Any) -> None:
        if not isinstance(request, dict):
            raise TypeError("stdin request must be a JSON object")
        action = str(request.get("action", "")).strip().lower()
        if action == "stop":
            self.stop_event.set()
            self.actions.put("stop")
        elif action == "resume":
            self.actions.put("resume")
        elif action == "notify":
            request_id, cue = request.get("id"), request.get("cue")
            valid_id = isinstance(request_id, str) and 0 < len(request_id) <= 128 and request_id.strip() == request_id
            valid_id = valid_id and not any(ord(character) < 32 for character in request_id)
            valid = valid_id and isinstance(cue, str) and cue in NOTIFICATION_CUES
            valid = valid and request.get("listen_after", "wake") == "wake"
            valid = valid and not (set(request) - {"action", "id", "cue", "listen_after"})
            if not valid:
                emit("notification", id=request_id if valid_id else "", cue=cue if isinstance(cue, str) else "",
                     stage="failed", error="invalid_notification")
                return
            try:
                self.notifications.put_nowait({"id": request_id, "cue": cue})
            except queue.Full:
                emit("notification", id=request_id, cue=cue, stage="failed", error="notification_queue_full")
        elif action:
            log(f"Ignoring unsupported action: {action}")

    def has_notifications(self) -> bool:
        return not self.notifications.empty()

    def next_notification(self) -> dict[str, str] | None:
        try:
            return self.notifications.get_nowait()
        except queue.Empty:
            return None

    def wait_for_resume(self, on_notification: Callable[[dict[str, str]], None] | None = None) -> str:
        while not self.stopped():
            # Control requests use a separate queue and always win over another
            # notification; a queued resume must never be consumed as audio work.
            try:
                action = self.actions.get_nowait()
            except queue.Empty:
                action = None
            if action in {"resume", "stop"}:
                return action
            notification = self.next_notification() if on_notification is not None else None
            if notification is not None:
                on_notification(notification)
                continue
            try:
                action = self.actions.get(timeout=0.1)
            except queue.Empty:
                continue
            if action in {"resume", "stop"}:
                return action
        return "stop"


class AudioCapture:
    def __init__(
        self,
        ffmpeg: str,
        input_device: str,
        sample_rate: int,
        source_rate: int,
        block_ms: int,
        input_gain_db: float,
    ) -> None:
        self.ffmpeg = ffmpeg
        self.input_device = input_device
        self.sample_rate = sample_rate
        self.source_rate = source_rate
        self.block_ms = block_ms
        self.input_gain_db = input_gain_db
        self.block_bytes = int(sample_rate * block_ms / 1000) * 2
        self.process: subprocess.Popen[bytes] | None = None
        # Always drain FFmpeg, even while ASR is busy. Keep at most one second
        # of fresh audio rather than allowing its pipe/DirectShow queue to grow.
        self.buffer_blocks = max(1, math.ceil(1000 / block_ms))
        self.blocks: queue.Queue[tuple[int, int, bytes] | Exception] = queue.Queue(self.buffer_blocks)
        self.buffer_generation = 0
        self.reader_stop = threading.Event()
        self.reader_thread: threading.Thread | None = None
        self.last_sequence: int | None = None
        self.dropped_blocks = 0
        self.stderr_lines: collections.deque[str] = collections.deque(maxlen=12)
        self.stderr_thread: threading.Thread | None = None

    @property
    def block_seconds(self) -> float:
        return self.block_ms / 1000.0

    def _dshow_input(self) -> str:
        value = self.input_device.strip()
        return value if value.lower().startswith("audio=") else f"audio={value}"

    def start(self) -> None:
        self.stop()
        self.blocks = queue.Queue(self.buffer_blocks)
        self.buffer_generation += 1
        self.reader_stop = threading.Event()
        self.last_sequence = None
        self.dropped_blocks = 0
        self.stderr_lines.clear()
        command = [
            self.ffmpeg,
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
            "-f",
            "dshow",
            "-thread_queue_size",
            "256",
            "-sample_rate",
            str(self.source_rate),
            "-sample_size",
            "16",
            "-channels",
            "1",
            "-audio_buffer_size",
            "50",
            "-i",
            self._dshow_input(),
            "-af",
            (
                "highpass=f=90,"
                f"volume={self.input_gain_db:g}dB,"
                "alimiter=limit=0.95:level=false"
            ),
            "-ac",
            "1",
            "-ar",
            str(self.sample_rate),
            "-f",
            "s16le",
            "pipe:1",
        ]
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.stderr_thread = threading.Thread(
            target=self._forward_stderr,
            args=(self.process,),
            name="voice-agent-ffmpeg-stderr",
            daemon=True,
        )
        self.stderr_thread.start()
        self.reader_thread = threading.Thread(
            target=self._read_audio,
            args=(self.process, self.blocks, self.reader_stop),
            name="voice-agent-ffmpeg-audio",
            daemon=True,
        )
        self.reader_thread.start()

    def _forward_stderr(self, process: subprocess.Popen[bytes]) -> None:
        if process is None or process.stderr is None:
            return
        try:
            for raw_line in iter(process.stderr.readline, b""):
                line = raw_line.decode("utf-8", errors="replace").rstrip()
                if line:
                    self.stderr_lines.append(line)
                    log(f"ffmpeg: {line}")
        except (OSError, ValueError):
            # Closing a stopped FFmpeg pipe may interrupt the reader.
            pass

    def _read_audio(
        self,
        process: subprocess.Popen[bytes],
        blocks: queue.Queue[tuple[int, int, bytes] | Exception],
        stopped: threading.Event,
    ) -> None:
        pending = bytearray()
        sequence = 0
        generation = self.buffer_generation

        def put_latest(item: tuple[int, int, bytes] | Exception) -> None:
            while not stopped.is_set():
                try:
                    blocks.put_nowait(item)
                    return
                except queue.Full:
                    try:
                        blocks.get_nowait()
                        self.dropped_blocks += 1
                    except queue.Empty:
                        pass

        try:
            assert process.stdout is not None
            while not stopped.is_set():
                if generation != self.buffer_generation:
                    pending.clear()
                    generation = self.buffer_generation
                part = process.stdout.read(self.block_bytes - len(pending))
                if not part:
                    detail = "；".join(self.stderr_lines)
                    suffix = f"：{detail}" if detail else ""
                    raise RuntimeError(f"FFmpeg 麦克风流已结束{suffix}")
                if generation != self.buffer_generation:
                    # The read straddled a prompt/resume boundary. It may contain
                    # old reply audio, including a partial block held locally.
                    pending.clear()
                    generation = self.buffer_generation
                    continue
                pending.extend(part)
                if len(pending) == self.block_bytes:
                    put_latest((generation, sequence, bytes(pending)))
                    sequence += 1
                    pending.clear()
        except Exception as exc:
            if not stopped.is_set():
                put_latest(exc)

    def read_block(self, should_stop: Callable[[], bool] | None = None) -> bytes:
        if self.process is None:
            raise RuntimeError("麦克风采集尚未启动")
        deadline = time.monotonic() + 5.0
        while not self.reader_stop.is_set():
            if should_stop is not None and should_stop():
                return b""
            try:
                item = self.blocks.get(timeout=0.1)
            except queue.Empty:
                if time.monotonic() >= deadline:
                    raise RuntimeError("麦克风连续 5 秒未返回音频，请检查设备连接")
                continue
            if isinstance(item, Exception):
                raise item
            generation, sequence, block = item
            if generation != self.buffer_generation:
                continue
            self.last_sequence = sequence
            return block
        raise RuntimeError("麦克风采集已停止")

    def clear_buffer(self) -> None:
        self.buffer_generation += 1
        for _ in range(self.buffer_blocks):
            try:
                item = self.blocks.get_nowait()
            except queue.Empty:
                break
            if isinstance(item, Exception):
                self.blocks.put_nowait(item)
                break
        self.last_sequence = None

    def drain(self, seconds: float, should_stop: Callable[[], bool]) -> None:
        block_count = max(0, math.ceil(seconds / self.block_seconds))
        for _ in range(block_count):
            if should_stop():
                return
            self.read_block(should_stop)

    def stop(self) -> None:
        process = self.process
        self.process = None
        self.reader_stop.set()
        if process is None:
            return
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
        if process.stdout is not None:
            process.stdout.close()
        if process.stderr is not None:
            process.stderr.close()
        for thread in (self.reader_thread, self.stderr_thread):
            if thread is not None:
                thread.join(timeout=1)
        self.reader_thread = None
        self.stderr_thread = None


class UtteranceTooLongError(RuntimeError):
    """A command reached its recording limit before a natural sentence end."""


class UtteranceDetector:
    def __init__(
        self,
        numpy_module: Any,
        capture: AudioCapture,
        threshold_dbfs: float,
        silence_ms: int,
        pre_roll_ms: int,
        minimum_speech_ms: int,
    ) -> None:
        self.np = numpy_module
        self.capture = capture
        self.threshold_dbfs = threshold_dbfs
        self.silence_blocks = max(1, math.ceil(silence_ms / capture.block_ms))
        self.pre_roll_blocks = max(0, math.ceil(pre_roll_ms / capture.block_ms))
        self.minimum_speech_blocks = max(1, math.ceil(minimum_speech_ms / capture.block_ms))

    def _is_speech(self, block: bytes) -> bool:
        values = self.np.frombuffer(block, dtype="<i2").astype(self.np.float32) / 32768.0
        if values.size == 0:
            return False
        rms = float(self.np.sqrt(self.np.mean(values * values)))
        level = 20.0 * math.log10(rms) if rms else -120.0
        return level > self.threshold_dbfs

    def next_utterance(
        self,
        max_utterance_seconds: float,
        max_wait_seconds: float,
        should_stop: Callable[[], bool],
        interrupt_idle: Callable[[], bool] | None = None,
        reject_truncated: bool = False,
    ) -> bytes | None:
        pre_roll: collections.deque[bytes] = collections.deque(maxlen=self.pre_roll_blocks)
        active: list[bytes] = []
        speech_blocks = 0
        quiet_blocks = 0
        max_blocks = max(1, math.ceil(max_utterance_seconds / self.capture.block_seconds))
        deadline = time.monotonic() + max_wait_seconds if max_wait_seconds > 0 else None
        previous_sequence: int | None = None

        while not should_stop():
            # The wait limit applies only before speech starts. A user who starts
            # near that deadline must still be able to finish a long instruction.
            if not active and deadline is not None and time.monotonic() >= deadline:
                break
            # Notifications may interrupt silence, never a started utterance.
            # This keeps a user's wake call or command intact while permitting
            # a status cue to be played promptly when the room is quiet.
            def interrupted() -> bool:
                return should_stop() or (not active and interrupt_idle is not None and interrupt_idle())
            if interrupted():
                break
            block = self.capture.read_block(interrupted)
            if should_stop():
                return None
            if not block:
                break
            sequence = self.capture.last_sequence
            if previous_sequence is not None and sequence != previous_sequence + 1:
                # Overflow means the consumer fell behind; do not splice speech
                # across a gap into a fabricated utterance.
                active.clear()
                pre_roll.clear()
                speech_blocks = quiet_blocks = 0
            previous_sequence = sequence
            above_threshold = self._is_speech(block)

            if active:
                active.append(block)
                if above_threshold:
                    speech_blocks += 1
                    quiet_blocks = 0
                else:
                    quiet_blocks += 1
            elif above_threshold:
                active = [*pre_roll, block]
                speech_blocks = 1
                quiet_blocks = 0
            else:
                pre_roll.append(block)
                continue

            if quiet_blocks < self.silence_blocks and len(active) < max_blocks:
                continue
            if reject_truncated and len(active) >= max_blocks and quiet_blocks < self.silence_blocks:
                raise UtteranceTooLongError("指令尚未说完，已达到单条语音时长上限")
            if speech_blocks >= self.minimum_speech_blocks:
                return b"".join(active)
            active.clear()
            pre_roll.clear()
            speech_blocks = 0
            quiet_blocks = 0

        # Stopping the worker must never publish whatever happened to be heard.
        if not should_stop() and speech_blocks >= self.minimum_speech_blocks:
            return b"".join(active)
        return None

    def discard_until_silence(self, should_stop: Callable[[], bool], max_wait_seconds: float = 0) -> bool:
        """Drain a rejected command's tail without recognizing or retaining it."""
        quiet_blocks = 0
        previous_sequence: int | None = None
        deadline = time.monotonic() + max_wait_seconds if max_wait_seconds > 0 else None
        while not should_stop() and quiet_blocks < self.silence_blocks:
            if deadline is not None and time.monotonic() >= deadline:
                return False
            block = self.capture.read_block(should_stop)
            if should_stop() or not block:
                return False
            sequence = self.capture.last_sequence
            if previous_sequence is not None and sequence != previous_sequence + 1:
                quiet_blocks = 0
            previous_sequence = sequence
            quiet_blocks = 0 if self._is_speech(block) else quiet_blocks + 1
        return not should_stop() and quiet_blocks >= self.silence_blocks


class Recognizer:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.np: Any = None
        self.model: Any = None

    def load(self) -> None:
        model_path = Path(self.args.model_path).expanduser().resolve()
        vad_path = Path(self.args.vad_model_path).expanduser().resolve()
        if not model_path.is_dir():
            raise FileNotFoundError(f"SenseVoice 模型目录不存在：{model_path}")
        if not vad_path.is_dir():
            raise FileNotFoundError(f"FSMN VAD 模型目录不存在：{vad_path}")

        # Some FunASR versions print status messages. Redirect them away from the
        # JSONL channel while importing, loading, and running inference.
        with contextlib.redirect_stdout(sys.stderr):
            import numpy as numpy_module
            from funasr import AutoModel

            self.np = numpy_module
            self.model = AutoModel(
                model=str(model_path),
                vad_model=str(vad_path),
                vad_kwargs={"max_single_segment_time": 30000},
                device="cpu",
                ncpu=self.args.ncpu,
                disable_update=True,
                disable_log=True,
                disable_pbar=True,
            )

    def recognize_pcm(self, pcm: bytes) -> tuple[str, str]:
        if self.model is None or self.np is None:
            raise RuntimeError("语音识别模型尚未加载")
        audio = self.np.frombuffer(pcm, dtype="<i2").astype(self.np.float32) / 32768.0
        audio, gain_db = normalize_recognition_audio(self.np, audio)
        started = time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            result = self.model.generate(
                input=audio,
                cache={},
                language="zh",
                use_itn=True,
                batch_size_s=30,
                merge_vad=False,
                disable_pbar=True,
            )
        if isinstance(result, dict):
            result = [result]
        raw = " ".join(
            str(item.get("text", ""))
            for item in result or []
            if isinstance(item, dict)
        ).strip()
        log(
            f"ASR: {len(pcm) / (self.args.sample_rate * 2):.2f}s audio, "
            f"gain +{gain_db:.1f}dB, {time.perf_counter() - started:.3f}s inference"
        )
        return clean_recognition_text(raw), raw


def normalize_recognition_audio(np: Any, audio: Any) -> tuple[Any, float]:
    """Boost quiet utterances without clipping or amplifying by more than 24 dB."""
    if not audio.size:
        return audio, 0.0
    rms = float(np.sqrt(np.mean(audio * audio)))
    peak = float(np.max(np.abs(audio)))
    if not math.isfinite(rms) or not math.isfinite(peak) or rms <= 1e-8:
        return audio, 0.0
    gain = max(1.0, min(10 ** (-24 / 20) / rms, 10 ** (24 / 20), 10 ** (-3 / 20) / peak))
    if gain == 1.0:
        return audio, 0.0
    return audio * gain, 20.0 * math.log10(gain)


def play_reply(path: Path) -> None:
    if os.name != "nt":
        raise RuntimeError("提示音播放仅支持 Windows")
    if not path.is_file():
        raise FileNotFoundError(f"提示音文件不存在：{path}")
    winmm = ctypes.WinDLL("winmm")
    winmm.PlaySoundW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint]
    winmm.PlaySoundW.restype = ctypes.c_int
    # SND_FILENAME | SND_NODEFAULT. Without SND_ASYNC this blocks until playback ends.
    if not winmm.PlaySoundW(str(path), None, 0x00020002):
        raise RuntimeError("Windows 无法播放唤醒提示音")


class NotificationSounds:
    """Fixed short PCM cues; stdin can never select an arbitrary file to play."""

    NOTES = {
        "progress": ((659, .08), (880, .10)),
        "waiting": ((440, .12), (659, .12), (440, .16)),
        "completed": ((523, .09), (659, .09), (784, .16)),
        "failed": ((659, .12), (392, .15), (262, .18)),
        "cancelled": ((523, .10), (392, .12)),
    }

    def __init__(self) -> None:
        self.cache = tempfile.TemporaryDirectory(prefix="halo-voice-notifications-")
        self.paths: dict[str, Path] = {}

    def path_for(self, cue: str) -> Path:
        group = NOTIFICATION_CUES[cue]
        if group in self.paths:
            return self.paths[group]
        sample_rate = 22050
        samples = array.array("h")
        fade_samples = max(1, round(sample_rate * .01))
        for frequency, seconds in self.NOTES[group]:
            count = round(sample_rate * seconds)
            for index in range(count):
                envelope = min(1.0, index / fade_samples, (count - 1 - index) / fade_samples)
                samples.append(round(32767 * .22 * envelope * math.sin(2 * math.pi * frequency * index / sample_rate)))
            samples.extend([0] * round(sample_rate * .035))
        if sys.byteorder != "little":
            samples.byteswap()
        path = Path(self.cache.name) / f"{group}.wav"
        with wave.open(str(path), "wb") as target:
            target.setnchannels(1)
            target.setsampwidth(2)
            target.setframerate(sample_rate)
            target.writeframes(samples.tobytes())
        self.paths[group] = path
        return path

    def play(self, cue: str) -> None:
        play_reply(self.path_for(cue))

    def close(self) -> None:
        self.cache.cleanup()


def decode_audio_file(ffmpeg: str, path: Path, sample_rate: int) -> bytes:
    if not path.is_file():
        raise FileNotFoundError(f"音频文件不存在：{path}")
    command = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostdin",
        "-i",
        str(path),
        "-ac",
        "1",
        "-ar",
        str(sample_rate),
        "-f",
        "s16le",
        "pipe:1",
    ]
    completed = subprocess.run(
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        timeout=60,
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"FFmpeg 无法读取音频文件：{detail}")
    if not completed.stdout:
        raise RuntimeError("音频文件没有可识别的采样")
    return completed.stdout


def validate_reply(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise FileNotFoundError(f"提示音文件不存在：{path}")
    with wave.open(str(path), "rb") as source:
        frames = source.getnframes()
        rate = source.getframerate()
        duration = frames / rate if rate else 0.0
        return {
            "reply_rate": rate,
            "reply_channels": source.getnchannels(),
            "reply_width": source.getsampwidth(),
            "reply_seconds": round(duration, 3),
        }


def check_ffmpeg(executable: str) -> None:
    try:
        completed = subprocess.run(
            [executable, "-version"],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            timeout=10,
            check=False,
        )
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"FFmpeg 不可用：{executable}") from exc
    if completed.returncode != 0:
        raise RuntimeError(f"FFmpeg 不可用（退出码 {completed.returncode}）：{executable}")


def run_self_test(args: argparse.Namespace) -> int:
    emit_state("loading", "正在检查语音 Agent 运行环境")
    check_ffmpeg(args.ffmpeg)
    reply_info = validate_reply(Path(args.reply).expanduser().resolve())
    processing_reply_info = validate_reply(
        Path(args.processing_reply).expanduser().resolve()
    )
    started = time.perf_counter()
    recognizer = Recognizer(args)
    recognizer.load()
    emit_state(
        "ready",
        "语音 Agent 运行环境可用",
        model_load_seconds=round(time.perf_counter() - started, 3),
        **reply_info,
        **{f"processing_{key}": value for key, value in processing_reply_info.items()},
    )
    return 0


def run_recognize_file(args: argparse.Namespace) -> int:
    emit_state("loading", "正在加载本地语音识别模型")
    recognizer = Recognizer(args)
    recognizer.load()
    emit_state("recognizing", "正在识别测试音频")
    pcm = decode_audio_file(args.ffmpeg, Path(args.recognize_file).expanduser().resolve(), args.sample_rate)
    text, raw = recognizer.recognize_pcm(pcm)
    matched = matches_wake_word(text)
    emit("command", text=text, raw=raw, matched_wake=matched)
    if args.expect_wake and not matched:
        emit("error", message="测试音频未识别出唤醒词“花再花再”")
        return 2
    emit_state("ready", "测试音频识别完成")
    return 0


def run_live(args: argparse.Namespace) -> int:
    actions = ActionChannel()
    capture: AudioCapture | None = None
    notification_sounds: NotificationSounds | None = None
    try:
        emit_state("loading", "正在加载 SenseVoice 与 FSMN VAD 模型")
        check_ffmpeg(args.ffmpeg)
        reply_path = Path(args.reply).expanduser().resolve()
        processing_reply_path = Path(args.processing_reply).expanduser().resolve()
        validate_reply(reply_path)
        validate_reply(processing_reply_path)
        recognizer = Recognizer(args)
        recognizer.load()
        # Importing FunASR/PyTorch can deadlock on Windows when another Python
        # thread is already blocked on a redirected stdin pipe. Load the model
        # before starting the control-channel reader; the parent still guards
        # startup with its own timeout and can terminate the process tree.
        actions.start()
        if actions.stopped():
            return 0

        capture = AudioCapture(
            args.ffmpeg,
            args.input_device,
            args.sample_rate,
            args.source_rate,
            args.block_ms,
            args.input_gain_db,
        )

        def start_fresh_capture(cooldown_ms: int, silence_ms: int) -> UtteranceDetector:
            assert capture is not None
            if capture.process is None:
                capture.start()
            capture.clear_buffer()
            capture.drain(cooldown_ms / 1000.0, actions.stopped)
            capture.clear_buffer()
            return UtteranceDetector(
                recognizer.np,
                capture,
                args.threshold_dbfs,
                silence_ms,
                args.pre_roll_ms,
                args.minimum_speech_ms,
            )

        wake_silence_ms = (
            args.silence_ms if args.silence_ms is not None else args.wake_silence_ms
        )
        command_silence_ms = (
            args.silence_ms if args.silence_ms is not None else args.command_silence_ms
        )

        detector = start_fresh_capture(args.startup_drain_ms, wake_silence_ms)
        notification_sounds = NotificationSounds()

        def play_notification(notification: dict[str, str]) -> None:
            assert capture is not None and notification_sounds is not None
            if actions.stopped():
                return
            capture.clear_buffer()
            emit("notification", **notification, stage="started")
            error = None
            try:
                notification_sounds.play(notification["cue"])
            except Exception as exc:
                error = str(exc)
                log(f"notification playback failed: {exc!r}")
            finally:
                # The FFmpeg reader stays warm during playback, but none of that
                # audio or its trailing room echo is eligible for wake/command ASR.
                capture.clear_buffer()
                if not actions.stopped():
                    capture.drain(args.resume_cooldown_ms / 1000.0, actions.stopped)
                capture.clear_buffer()
            if not actions.stopped():
                emit("notification", **notification, stage="failed" if error else "played",
                     **({"error": error} if error else {}))

        emit_state("ready", "正在等待“花再花再”")

        while not actions.stopped():
            notification = actions.next_notification()
            if notification is not None:
                play_notification(notification)
                if actions.stopped():
                    break
                detector = start_fresh_capture(0, wake_silence_ms)
                emit_state("ready", "正在等待“花再花再”")
                continue
            wake_pcm = detector.next_utterance(
                args.wake_max_seconds,
                args.wake_timeout_seconds,
                actions.stopped,
                interrupt_idle=actions.has_notifications,
            )
            if actions.stopped():
                break
            if wake_pcm is None:
                if actions.has_notifications():
                    continue
                emit_state("ready", "等待唤醒超时，继续监听“花再花再”")
                continue

            # Background wake checks must keep the waiting status stable. Only
            # an accepted wake word advances the visible interaction phase.
            wake_text, wake_raw = recognizer.recognize_pcm(wake_pcm)
            if not matches_wake_word(wake_text):
                emit_state("ready", "正在等待“花再花再”", last_heard=wake_text)
                continue

            emit_state(
                "wake_detected",
                "已识别唤醒词“花再花再”",
                text=wake_text,
                raw=wake_raw,
            )
            emit_state("cooldown", "正在播放“我在”并清空麦克风缓冲")
            play_reply(reply_path)
            if actions.stopped():
                break

            detector = start_fresh_capture(args.reply_cooldown_ms, command_silence_ms)
            emit_state("listening_command", "请说出指令")

            command_deadline = time.monotonic() + args.command_wait_seconds
            command_sent = False
            command_rejection = None
            while not actions.stopped() and time.monotonic() < command_deadline:
                remaining = max(0.1, command_deadline - time.monotonic())
                try:
                    command_pcm = detector.next_utterance(
                        args.command_max_seconds,
                        remaining,
                        actions.stopped,
                        reject_truncated=True,
                    )
                except UtteranceTooLongError:
                    command_rejection = (
                        f"指令超过 {args.command_max_seconds:g} 秒，尚未提交。"
                        "请等说话结束后重新唤醒，将任务分段说明，或在设置中延长单条语音时长。"
                    )
                    emit_state("command_rejected", command_rejection)
                    # Do not mistake the rest of this same sentence for a wake
                    # call or another instruction; no ASR or acknowledgements.
                    while not actions.stopped() and not detector.discard_until_silence(
                        actions.stopped, max_wait_seconds=max(15, args.command_max_seconds)
                    ):
                        # Continuous music/noise must not accidentally become a
                        # command. Keep no recording, and report the quiet wait
                        # after each bounded drain interval; stop remains usable.
                        emit_state("command_rejected", command_rejection + " 当前仍有持续声音，正在等待安静后恢复唤醒；也可停止监听。")
                    break
                if actions.stopped():
                    break
                if command_pcm is None:
                    break
                emit_state("recognizing", "正在识别指令")
                command_text, command_raw = recognizer.recognize_pcm(command_pcm)
                if actions.stopped():
                    break
                if not command_text:
                    emit_state("listening_command", "没有听清，请再说一次")
                    continue

                emit("command", text=command_text, raw=command_raw)
                command_sent = True
                play_reply(processing_reply_path)
                action = actions.wait_for_resume(play_notification)
                if action == "stop":
                    return 0
                emit_state("cooldown", "正在清空麦克风缓冲")
                detector = start_fresh_capture(args.resume_cooldown_ms, wake_silence_ms)
                emit_state("ready", "正在等待“花再花再”")
                break

            if actions.stopped():
                break
            if not command_sent:
                emit_state("cooldown", command_rejection or "未听到有效指令，正在恢复唤醒监听")
                detector = start_fresh_capture(args.resume_cooldown_ms, wake_silence_ms)
                emit_state("ready", command_rejection or "正在等待“花再花再”")
        return 0
    finally:
        if capture is not None:
            capture.stop()
        if notification_sounds is not None:
            notification_sounds.close()


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Halo PixelBar 本地语音 Agent worker")
    parser.add_argument("--ffmpeg", default="ffmpeg", help="FFmpeg 可执行文件路径")
    parser.add_argument("--input-device", default=DEFAULT_INPUT_DEVICE, help="DirectShow 麦克风名称")
    parser.add_argument("--reply", default=str(DEFAULT_REPLY), help="唤醒回应 WAV 文件")
    parser.add_argument(
        "--processing-reply",
        default=str(DEFAULT_PROCESSING_REPLY),
        help="收到指令后立即播放的 WAV 文件",
    )
    parser.add_argument("--model-path", default=str(DEFAULT_MODEL), help="SenseVoiceSmall 本地目录")
    parser.add_argument("--vad-model-path", default=str(DEFAULT_VAD_MODEL), help="FSMN VAD 本地目录")
    parser.add_argument("--sample-rate", type=int, default=16000)
    parser.add_argument("--source-rate", type=int, default=48000)
    parser.add_argument("--block-ms", type=int, default=100)
    parser.add_argument("--input-gain-db", type=float, default=10.0)
    parser.add_argument("--threshold-dbfs", type=float, default=-52.0)
    parser.add_argument(
        "--silence-ms",
        type=int,
        default=None,
        help="兼容旧调用：同时覆盖唤醒词和指令的静音断句时间",
    )
    parser.add_argument("--wake-silence-ms", type=int, default=400)
    parser.add_argument("--command-silence-ms", type=int, default=1800)
    parser.add_argument("--pre-roll-ms", type=int, default=600)
    parser.add_argument("--minimum-speech-ms", type=int, default=300)
    parser.add_argument("--wake-max-seconds", type=float, default=5.0)
    parser.add_argument("--wake-timeout-seconds", type=float, default=0.0)
    parser.add_argument("--command-max-seconds", type=float, default=60.0)
    parser.add_argument("--command-wait-seconds", type=float, default=15.0)
    parser.add_argument("--startup-drain-ms", type=int, default=300)
    # Capture stays warm while the prompt plays. Drop its buffered echo at the
    # boundary instead of reopening the device and losing the command's onset.
    parser.add_argument("--reply-cooldown-ms", type=int, default=0)
    parser.add_argument("--resume-cooldown-ms", type=int, default=400)
    parser.add_argument("--ncpu", type=int, default=min(8, max(1, os.cpu_count() or 1)))
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--self-test", action="store_true", help="检查依赖并实际加载模型后退出")
    mode.add_argument("--recognize-file", metavar="PATH", help="离线识别一个音频文件后退出")
    parser.add_argument("--expect-wake", action="store_true", help="离线识别时要求命中唤醒词")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.self_test:
            return run_self_test(args)
        if args.recognize_file:
            return run_recognize_file(args)
        return run_live(args)
    except KeyboardInterrupt:
        return 0
    except Exception as exc:
        log(f"voice-agent error: {exc!r}")
        emit("error", message=str(exc))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
