"""Offline regressions; never opens a microphone, plays audio, or controls a device.

Run with the same Python/numpy environment as the voice worker:
    python -B integrations/voice-agent/tests/test_voice_agent_host.py
"""

import importlib.util
import io
import json
from pathlib import Path
import queue
import sys
import threading
import time
import unittest
import wave
from unittest import mock

import numpy as np

sys.dont_write_bytecode = True
WORKSPACE = Path(__file__).resolve().parents[3]
WORKER_PATH = WORKSPACE / "HaloPixelToolBox/HaloPixelToolBox/Assets/VoiceAgent/voice_agent_host.py"
spec = importlib.util.spec_from_file_location("voice_agent_host", WORKER_PATH)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class ControlledPipe:
    def __init__(self):
        self.parts = queue.Queue()
        self.pending = b""
        self.closed = False
        self.read_count = 0

    def read(self, count):
        self.read_count += 1
        if not self.pending:
            part = self.parts.get(timeout=5)
            if part is None:
                return b""
            self.pending = part
        result, self.pending = self.pending[:count], self.pending[count:]
        return result

    def feed(self, part):
        self.parts.put(part)

    def close(self):
        if not self.closed:
            self.closed = True
            self.parts.put(None)


class FakeProcess:
    def __init__(self):
        self.stdout = ControlledPipe()
        self.stderr = io.BytesIO()
        self.exited = False

    def poll(self):
        return 0 if self.exited else None

    def terminate(self):
        self.exited = True
        self.stdout.close()

    kill = terminate

    def wait(self, timeout):
        return 0


def wait_until(predicate):
    deadline = time.monotonic() + 2
    while not predicate():
        if time.monotonic() > deadline:
            raise AssertionError("background reader did not finish")
        time.sleep(0.005)


class FrameCapture:
    """A deterministic 100 ms microphone; its clock never sleeps."""

    block_ms, block_seconds, last_sequence = 100, .1, None

    def __init__(self, frames, stop_after=None):
        self.frames = iter(enumerate(frames))
        self.reads = 0
        self.clock = 0.0
        self.stop_after = stop_after
        self.stopped = False

    def read_block(self, should_stop):
        if should_stop():
            return b""
        self.last_sequence, block = next(self.frames, (None, b""))
        self.reads += 1
        self.clock = self.reads * self.block_seconds
        if self.stop_after is not None and self.reads >= self.stop_after:
            self.stopped = True
        return block


class VoiceWorkerTests(unittest.TestCase):
    def make_capture(self):
        return worker.AudioCapture("unused", "unused", 16000, 48000, 100, 10)

    def sentence_frames(self, speech_blocks, silence_blocks):
        speech = np.full(1600, 2000, dtype="<i2").tobytes()
        return [speech] * speech_blocks + [bytes(3200)] * silence_blocks

    def command_detector(self, capture):
        return worker.UtteranceDetector(np, capture, -52, 1800, 600, 300)

    def test_default_command_endpoint_allows_pauses_without_slowing_wake(self):
        args = worker.build_parser().parse_args([])
        self.assertEqual(args.wake_silence_ms, 400)
        self.assertEqual(args.command_silence_ms, 1800)
        self.assertEqual(args.command_max_seconds, 60)

    def test_command_endpoint_and_length_can_be_configured_independently(self):
        args = worker.build_parser().parse_args(["--command-silence-ms", "4000", "--command-max-seconds", "120"])
        self.assertEqual(args.command_silence_ms, 4000)
        self.assertEqual(args.command_max_seconds, 120)
        self.assertEqual(args.wake_silence_ms, 400)
        self.assertIsNone(args.silence_ms)

    def test_temporary_pause_does_not_cut_a_long_command(self):
        frames = self.sentence_frames(10, 17) + self.sentence_frames(10, 18)
        capture = FrameCapture(frames)
        result = self.command_detector(capture).next_utterance(60, 15, lambda: False, reject_truncated=True)
        self.assertEqual(capture.reads, len(frames))
        self.assertEqual(result, b"".join(frames))

    def test_speech_can_continue_past_old_twelve_and_fifteen_second_limits(self):
        frames = self.sentence_frames(200, 18)
        capture = FrameCapture(frames)
        with mock.patch.object(worker.time, "monotonic", side_effect=lambda: capture.clock):
            result = self.command_detector(capture).next_utterance(60, 15, lambda: False, reject_truncated=True)
        self.assertEqual(result, b"".join(frames))
        self.assertEqual(capture.reads, 218)

    def test_start_near_idle_deadline_still_gets_full_sentence(self):
        frames = self.sentence_frames(0, 140) + self.sentence_frames(200, 18)
        capture = FrameCapture(frames)
        with mock.patch.object(worker.time, "monotonic", side_effect=lambda: capture.clock):
            result = self.command_detector(capture).next_utterance(60, 15, lambda: False, reject_truncated=True)
        self.assertEqual(capture.reads, 358)
        self.assertEqual(result, b"".join(frames[134:]))  # The 600 ms pre-roll remains intact.

    def test_idle_wait_still_times_out_without_a_command(self):
        capture = FrameCapture(self.sentence_frames(0, 200))
        with mock.patch.object(worker.time, "monotonic", side_effect=lambda: capture.clock):
            result = self.command_detector(capture).next_utterance(60, 15, lambda: False, reject_truncated=True)
        self.assertIsNone(result)
        self.assertEqual(capture.reads, 150)

    def test_hard_limit_rejects_speech_instead_of_returning_a_partial_command(self):
        capture = FrameCapture(self.sentence_frames(40, 18))
        with self.assertRaises(worker.UtteranceTooLongError):
            self.command_detector(capture).next_utterance(2, 15, lambda: False, reject_truncated=True)
        self.assertEqual(capture.reads, 20)

    def test_natural_sentence_end_at_limit_is_not_rejected(self):
        frames = self.sentence_frames(10, 18)
        capture = FrameCapture(frames)
        result = self.command_detector(capture).next_utterance(2.8, 15, lambda: False, reject_truncated=True)
        self.assertEqual(result, b"".join(frames))

    def test_stop_discards_partial_speech_including_last_read_block(self):
        capture = FrameCapture(self.sentence_frames(20, 18), stop_after=5)
        result = self.command_detector(capture).next_utterance(60, 15, lambda: capture.stopped, reject_truncated=True)
        self.assertIsNone(result)
        self.assertEqual(capture.reads, 5)

    def test_rejected_command_tail_is_discarded_until_a_real_pause(self):
        frames = self.sentence_frames(10, 17) + self.sentence_frames(10, 18) + self.sentence_frames(10, 18)
        capture = FrameCapture(frames)
        self.assertTrue(self.command_detector(capture).discard_until_silence(lambda: False))
        self.assertEqual(capture.reads, 55)

    def test_stop_can_interrupt_discarding_a_rejected_command(self):
        capture = FrameCapture(self.sentence_frames(200, 18), stop_after=5)
        self.assertFalse(self.command_detector(capture).discard_until_silence(lambda: capture.stopped))
        self.assertEqual(capture.reads, 5)

    def test_continuous_background_sound_has_a_bounded_discard_interval(self):
        capture = FrameCapture(self.sentence_frames(200, 18))
        with mock.patch.object(worker.time, "monotonic", side_effect=lambda: capture.clock):
            quiet = self.command_detector(capture).discard_until_silence(lambda: False, max_wait_seconds=1)
        self.assertFalse(quiet)
        self.assertEqual(capture.reads, 10)

    def test_wake_accepts_bounded_repetition_variants_and_fillers(self):
        positives = (*worker.WAKE_ALIASES, "花再花再，花再花再", "華在華仔", "啊花再花再",
                     "华仔华仔啊", "好在好仔好在好仔", "喂，花再花再呀", "花再花再" * 3)
        for text in positives:
            with self.subTest(text=text):
                self.assertTrue(worker.matches_wake_word(text))

    def test_wake_rejects_ordinary_sentences_and_reply_echo(self):
        negatives = ("", "花再", "我在", "我在我在", "收到", "好的好的", "你好", "花再花再关灯",
                     "请问花再花再是什么意思", "我说花再花再你怎么没回答", "花再花再" * 4,
                     "啊啊啊花再花再", "好在今天没下雨", "花再花再查询当前任务状态")
        for text in negatives:
            with self.subTest(text=text):
                self.assertFalse(worker.matches_wake_word(text))

    def test_quiet_audio_gain_is_bounded_and_peak_safe(self):
        quiet = np.full(1600, 0.0001, dtype=np.float32)
        boosted, gain = worker.normalize_recognition_audio(np, quiet)
        self.assertAlmostEqual(gain, 24)
        self.assertLessEqual(float(np.max(np.abs(boosted))), 10 ** (-3 / 20))
        transient = quiet.copy()
        transient[0] = 0.1
        boosted, _ = worker.normalize_recognition_audio(np, transient)
        self.assertLessEqual(float(np.max(np.abs(boosted))), 10 ** (-3 / 20) + 1e-7)

    def test_silent_and_already_loud_audio_are_unchanged(self):
        for audio in (np.zeros(1600, dtype=np.float32), np.full(1600, 0.25, dtype=np.float32),
                      np.empty(0, dtype=np.float32)):
            result, gain = worker.normalize_recognition_audio(np, audio)
            np.testing.assert_array_equal(audio, result)
            self.assertEqual(gain, 0)

    def test_background_reader_retains_latest_second_during_asr_pause(self):
        capture, process = self.make_capture(), FakeProcess()
        with mock.patch.object(worker.subprocess, "Popen", return_value=process):
            capture.start()
        try:
            for number in range(25):
                process.stdout.feed(bytes([number]) * capture.block_bytes)
            wait_until(lambda: capture.dropped_blocks == 15)
            self.assertEqual(capture.blocks.qsize(), 10)
            self.assertEqual(capture.read_block(), bytes([15]) * capture.block_bytes)
            self.assertEqual(capture.last_sequence, 15)
            capture.clear_buffer()
            # The in-flight read is discarded at the boundary, at most 100 ms.
            process.stdout.feed(bytes([98]) * capture.block_bytes)
            process.stdout.feed(bytes([99]) * capture.block_bytes)
            self.assertEqual(capture.read_block(), bytes([99]) * capture.block_bytes)
        finally:
            reader = capture.reader_thread
            capture.stop()
            self.assertFalse(reader.is_alive())

    def test_boundary_flush_discards_partial_prompt_and_inflight_read(self):
        capture, process = self.make_capture(), FakeProcess()
        with mock.patch.object(worker.subprocess, "Popen", return_value=process):
            capture.start()
        try:
            process.stdout.feed(b"a" * (capture.block_bytes // 2))
            wait_until(lambda: process.stdout.read_count == 2)
            capture.clear_buffer()
            process.stdout.feed(b"b" * (capture.block_bytes // 2))
            process.stdout.feed(b"c" * capture.block_bytes)
            self.assertEqual(capture.read_block(), b"c" * capture.block_bytes)
        finally:
            capture.stop()

    def test_restart_cannot_reuse_previous_capture_audio(self):
        capture = self.make_capture()
        old, new = FakeProcess(), FakeProcess()
        with mock.patch.object(worker.subprocess, "Popen", side_effect=[old, new]):
            capture.start()
            old.stdout.feed(b"a" * capture.block_bytes)
            wait_until(lambda: not capture.blocks.empty())
            old_reader = capture.reader_thread
            capture.start()
            try:
                self.assertFalse(old_reader.is_alive())
                new.stdout.feed(b"b" * capture.block_bytes)
                self.assertEqual(capture.read_block(), b"b" * capture.block_bytes)
                self.assertEqual(capture.last_sequence, 0)
            finally:
                capture.stop()

    def test_no_audio_read_can_be_cancelled_promptly(self):
        capture, process = self.make_capture(), FakeProcess()
        with mock.patch.object(worker.subprocess, "Popen", return_value=process):
            capture.start()
        stopped = threading.Event()
        timer = threading.Timer(0.05, stopped.set)
        try:
            timer.start()
            started = time.monotonic()
            self.assertEqual(capture.read_block(stopped.is_set), b"")
            self.assertLess(time.monotonic() - started, 0.5)
        finally:
            timer.cancel()
            capture.stop()

    def test_eof_is_reported_instead_of_hanging(self):
        capture, process = self.make_capture(), FakeProcess()
        with mock.patch.object(worker.subprocess, "Popen", return_value=process):
            capture.start()
        try:
            process.stdout.close()
            with self.assertRaisesRegex(RuntimeError, "麦克风流已结束"):
                capture.read_block()
        finally:
            capture.stop()

    def test_utterance_cannot_join_speech_across_dropped_blocks(self):
        speech = np.full(1600, 2000, dtype="<i2").tobytes()
        silence = bytes(3200)

        class Capture:
            block_ms, block_seconds, last_sequence = 100, 0.1, None

            def __init__(self):
                self.frames = iter(((0, speech), (2, speech), (3, silence), (4, silence)))

            def read_block(self, should_stop):
                self.last_sequence, block = next(self.frames, (None, b""))
                return block

        detector = worker.UtteranceDetector(np, Capture(), -40, 200, 0, 200)
        self.assertIsNone(detector.next_utterance(5, 0, lambda: False))

    def test_prompt_and_dsh_pause_keep_capture_warm_with_boundary_flush(self):
        args = worker.build_parser().parse_args([])
        capture = mock.Mock()
        capture.process = None
        capture.start.side_effect = lambda: setattr(capture, "process", object())
        actions = mock.Mock()
        actions.stopped.return_value = False
        actions.has_notifications.return_value = False
        actions.next_notification.return_value = None
        actions.wait_for_resume.return_value = "stop"
        recognizer = mock.Mock()
        recognizer.recognize_pcm.side_effect = [("花再花再", "wake"), ("关灯", "command")]
        detector = mock.Mock()
        detector.next_utterance.side_effect = [b"wake", b"command"]
        with mock.patch.object(worker, "ActionChannel", return_value=actions), \
                mock.patch.object(worker, "AudioCapture", return_value=capture), \
                mock.patch.object(worker, "Recognizer", return_value=recognizer), \
                mock.patch.object(worker, "UtteranceDetector", return_value=detector), \
                mock.patch.object(worker, "check_ffmpeg"), mock.patch.object(worker, "validate_reply"), \
                mock.patch.object(worker, "emit"), mock.patch.object(worker, "emit_state"), \
                mock.patch.object(worker, "play_reply") as reply:
            self.assertEqual(worker.run_live(args), 0)
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)  # Only final shutdown.
        self.assertEqual(reply.call_count, 2)
        self.assertEqual(capture.clear_buffer.call_count, 4)  # Startup and prompt boundaries.


class NotificationTests(unittest.TestCase):
    def notification(self, cue="task_completed", request_id="notification-1"):
        return {"action": "notify", "id": request_id, "cue": cue, "listen_after": "wake"}

    def test_only_fixed_cue_protocol_is_accepted(self):
        actions = worker.ActionChannel()
        for index, cue in enumerate(worker.NOTIFICATION_CUES):
            actions._accept(self.notification(cue, str(index)))
        self.assertEqual(actions.notifications.qsize(), len(worker.NOTIFICATION_CUES))
        self.assertTrue(actions.has_notifications())
        with mock.patch.object(worker, "emit") as emit:
            for invalid in (
                self.notification("unknown"), self.notification(request_id=""), self.notification(request_id="id\n"),
                self.notification(request_id=123), self.notification(request_id="x" * 129),
                {**self.notification(), "listen_after": "command"},
                {**self.notification(), "wav_path": r"C:\arbitrary.wav"},
            ):
                actions._accept(invalid)
        self.assertEqual(emit.call_count, 7)
        for call in emit.call_args_list:
            self.assertEqual(call.args, ("notification",))
            self.assertEqual(call.kwargs["stage"], "failed")
            self.assertEqual(call.kwargs["error"], "invalid_notification")
        self.assertFalse(actions.stopped())

    def test_stdin_notify_enqueue_does_not_play_or_swallow_controls(self):
        actions = worker.ActionChannel()
        stream = "invalid json\n[]\n" + json.dumps(self.notification()) + '\n{"action":"resume"}\n{"action":"stop"}\n'
        with mock.patch.object(worker.sys, "stdin", io.StringIO(stream)), mock.patch.object(worker, "play_reply") as play:
            actions._read()
        self.assertTrue(actions.stopped())
        self.assertEqual(actions.next_notification(), {"id": "notification-1", "cue": "task_completed"})
        self.assertEqual(actions.actions.get_nowait(), "resume")
        self.assertEqual(actions.actions.get_nowait(), "stop")
        play.assert_not_called()

    def test_notification_queue_is_bounded_and_controls_take_priority(self):
        actions = worker.ActionChannel()
        with mock.patch.object(worker, "emit") as emit:
            for index in range(17):
                actions._accept(self.notification(request_id=str(index)))
        self.assertEqual(actions.notifications.qsize(), 16)
        self.assertEqual(emit.call_args.kwargs["error"], "notification_queue_full")
        actions._accept({"action": "resume"})
        callback = mock.Mock()
        self.assertEqual(actions.wait_for_resume(callback), "resume")
        callback.assert_not_called()
        self.assertEqual(actions.notifications.qsize(), 16)
        actions._accept({"action": "stop"})
        self.assertEqual(actions.wait_for_resume(callback), "stop")
        callback.assert_not_called()

    def test_wait_for_resume_can_play_notification_then_keep_resume(self):
        actions = worker.ActionChannel()
        actions._accept(self.notification("approval_required"))
        def receive(notification):
            self.assertEqual(notification["cue"], "approval_required")
            actions._accept({"action": "resume"})
        callback = mock.Mock(side_effect=receive)
        self.assertEqual(actions.wait_for_resume(callback), "resume")
        callback.assert_called_once()

    def test_idle_notification_interrupts_silence_without_reading_audio(self):
        capture = mock.Mock(block_ms=100, block_seconds=.1)
        detector = worker.UtteranceDetector(np, capture, -40, 200, 0, 200)
        self.assertIsNone(detector.next_utterance(5, 0, lambda: False, interrupt_idle=lambda: True))
        capture.read_block.assert_not_called()

    def test_notification_does_not_cut_started_utterance(self):
        speech = np.full(1600, 2000, dtype="<i2").tobytes()
        silence = bytes(3200)
        pending = False
        class Capture:
            block_ms, block_seconds, last_sequence = 100, .1, None
            def __init__(self):
                self.frames = iter(enumerate((speech, speech, silence, silence)))
            def read_block(self, should_stop):
                nonlocal pending
                if should_stop():
                    return b""
                self.last_sequence, data = next(self.frames, (None, b""))
                pending = True
                return data
        detector = worker.UtteranceDetector(np, Capture(), -40, 200, 0, 200)
        result = detector.next_utterance(5, 0, lambda: False, interrupt_idle=lambda: pending)
        self.assertEqual(result, speech + speech + silence + silence)

    def test_cues_are_short_pcm_distinct_and_cached_without_existing_replies(self):
        sounds = worker.NotificationSounds()
        root = Path(sounds.cache.name).resolve()
        self.assertTrue(root.name.startswith("halo-voice-notifications-"))
        try:
            bodies = {}
            for cue in worker.NOTIFICATION_CUES:
                path = sounds.path_for(cue)
                self.assertEqual(path.parent.resolve(), root)
                self.assertEqual(sounds.path_for(cue), path)
                with wave.open(str(path), "rb") as source:
                    self.assertEqual((source.getnchannels(), source.getsampwidth(), source.getframerate()), (1, 2, 22050))
                    self.assertLess(source.getnframes() / source.getframerate(), 1)
                    bodies[cue] = source.readframes(source.getnframes())
            self.assertEqual(bodies["task_started"], bodies["task_progress"])
            self.assertEqual(bodies["approval_required"], bodies["input_required"])
            self.assertEqual(len({bodies[cue] for cue in ("task_progress", "approval_required", "task_completed", "task_failed", "task_cancelled")}), 5)
        finally:
            sounds.close()
        self.assertFalse(root.exists())

    def run_fake_live(self, actions, detector, recognizer, sounds):
        args = worker.build_parser().parse_args([])
        capture = mock.Mock()
        capture.process = None
        capture.start.side_effect = lambda: setattr(capture, "process", object())
        actions.start = mock.Mock()
        events = []
        capture.state_events = []
        def record(event_type, **payload):
            events.append({"type": event_type, **payload})
        def record_state(state, message, **payload):
            capture.state_events.append({"state": state, "message": message, **payload})
        with mock.patch.object(worker, "ActionChannel", return_value=actions), \
                mock.patch.object(worker, "AudioCapture", return_value=capture), \
                mock.patch.object(worker, "Recognizer", return_value=recognizer), \
                mock.patch.object(worker, "UtteranceDetector", return_value=detector), \
                mock.patch.object(worker, "NotificationSounds", return_value=sounds), \
                mock.patch.object(worker, "check_ffmpeg"), mock.patch.object(worker, "validate_reply"), \
                mock.patch.object(worker, "emit", side_effect=record), mock.patch.object(worker, "emit_state", side_effect=record_state), \
                mock.patch.object(worker, "play_reply") as reply:
            result = worker.run_live(args)
        return result, capture, events, reply

    def test_background_wake_checks_keep_waiting_until_a_real_wake_and_command(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        utterances = iter([b"music", b"speech", b"music", b"wake", b"command"])
        def audio(*args, **kwargs):
            try:
                return next(utterances)
            except StopIteration:
                actions._accept({"action": "stop"})
                return None
        def recognize(pcm):
            if pcm == b"wake":
                return "花再花再", "wake"
            if pcm == b"command":
                actions._accept({"action": "resume"})
                return "把音量调到十三", "command"
            return "背景音乐或普通说话", "background"
        detector.next_utterance.side_effect = audio
        recognizer.recognize_pcm.side_effect = recognize
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        states = [event["state"] for event in capture.state_events]
        wake_index = states.index("wake_detected")
        self.assertEqual(result, 0)
        self.assertEqual(states[1:wake_index], ["ready"] * 4)
        self.assertEqual(states.count("recognizing"), 1)
        self.assertGreater(states.index("recognizing"), states.index("listening_command"))
        self.assertEqual(recognizer.recognize_pcm.call_count, 5)
        self.assertEqual([event["text"] for event in events if event["type"] == "command"], ["把音量调到十三"])
        self.assertEqual(reply.call_count, 2)
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)

    def test_live_notification_serial_playback_flushes_echo_and_keeps_capture_warm(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        actions._accept(self.notification("approval_required", "waiting"))
        actions._accept(self.notification("task_completed", "complete"))
        def stop_detector(*args, **kwargs):
            actions._accept({"action": "stop"})
            return None
        detector.next_utterance.side_effect = stop_detector
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertEqual([call.args[0] for call in sounds.play.call_args_list], ["approval_required", "task_completed"])
        self.assertEqual([(event["id"], event["stage"]) for event in events], [("waiting", "started"), ("waiting", "played"), ("complete", "started"), ("complete", "played")])
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)
        self.assertEqual([call.args[0] for call in capture.drain.call_args_list], [.3, .4, 0, .4, 0])
        # Three boundary flushes per cue, plus two for each fresh detector.
        self.assertEqual(capture.clear_buffer.call_count, 12)
        recognizer.recognize_pcm.assert_not_called()
        reply.assert_not_called()

    def test_overlong_command_with_continuous_noise_stays_discarded_and_can_stop(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        detector.next_utterance.side_effect = [b"wake", worker.UtteranceTooLongError("recording limit")]
        recognizer.recognize_pcm.return_value = ("花再花再", "wake")
        drains = 0
        def drain(*args, **kwargs):
            nonlocal drains
            drains += 1
            self.assertEqual(kwargs["max_wait_seconds"], 60)
            if drains == 2:
                actions._accept({"action": "stop"})
            return False
        detector.discard_until_silence.side_effect = drain
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertEqual(detector.discard_until_silence.call_count, 2)
        self.assertFalse(any(event["type"] == "command" for event in events))
        self.assertEqual(reply.call_count, 1)
        recognizer.recognize_pcm.assert_called_once_with(b"wake")
        self.assertEqual(capture.stop.call_count, 1)
        self.assertTrue(any("正在等待安静" in state["message"] for state in capture.state_events))

    def test_notification_arriving_during_command_is_deferred_without_losing_resume(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        turns = 0
        def audio(*args, **kwargs):
            nonlocal turns
            turns += 1
            if turns == 1:
                return b"wake"
            if turns == 2:
                actions._accept(self.notification("input_required"))
                sounds.play.assert_not_called()
                return b"command"
            actions._accept({"action": "stop"})
            return None
        detector.next_utterance.side_effect = audio
        def recognize(pcm):
            sounds.play.assert_not_called()
            return ("花再花再", "wake") if pcm == b"wake" else ("关灯", "command")
        recognizer.recognize_pcm.side_effect = recognize
        sounds.play.side_effect = lambda cue: actions._accept({"action": "resume"})
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertEqual([(event["type"], event.get("stage")) for event in events], [("command", None), ("notification", "started"), ("notification", "played")])
        self.assertEqual([call.args[0] for call in recognizer.recognize_pcm.call_args_list], [b"wake", b"command"])
        self.assertEqual(reply.call_count, 2)  # Original wake and received replies.
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(actions.actions.get_nowait(), "stop")
        self.assertTrue(actions.actions.empty())  # Resume was used; only the final stop remained.

    def test_overlong_command_never_emits_command_or_plays_received_reply(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        turns = 0
        def audio(*args, **kwargs):
            nonlocal turns
            turns += 1
            if turns == 1:
                return b"wake"
            if turns == 2:
                self.assertTrue(kwargs["reject_truncated"])
                raise worker.UtteranceTooLongError("recording limit")
            actions._accept({"action": "stop"})
            return None
        detector.next_utterance.side_effect = audio
        recognizer.recognize_pcm.return_value = ("花再花再", "wake")
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertFalse(any(event["type"] == "command" for event in events))
        self.assertEqual(reply.call_count, 1)  # Only the wake acknowledgement.
        recognizer.recognize_pcm.assert_called_once_with(b"wake")
        detector.discard_until_silence.assert_called_once()
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)
        self.assertTrue(any(state["state"] == "command_rejected" and "尚未提交" in state["message"] for state in capture.state_events))
        self.assertTrue(any(state["state"] == "ready" and "尚未提交" in state["message"] for state in capture.state_events))

    def test_stop_during_command_recognition_never_emits_or_acknowledges(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        detector.next_utterance.side_effect = [b"wake", b"command"]
        def recognize(pcm):
            if pcm == b"wake":
                return "花再花再", "wake"
            actions._accept({"action": "stop"})
            return "任务内容尚未说完", "command"
        recognizer.recognize_pcm.side_effect = recognize
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertFalse(any(event["type"] == "command" for event in events))
        self.assertEqual(reply.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)

    def test_cue_failure_is_nonfatal_and_does_not_block_next_notification(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        actions._accept(self.notification("task_failed", "failed-cue"))
        actions._accept(self.notification("task_completed", "next-cue"))
        sounds.play.side_effect = [RuntimeError("audio stub failure"), None]
        def stop(*args, **kwargs):
            actions.stop_event.set()
            return None
        detector.next_utterance.side_effect = stop
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        self.assertEqual([event["stage"] for event in events], ["started", "failed", "started", "played"])
        self.assertEqual(events[1]["error"], "audio stub failure")
        self.assertFalse(any(event["type"] == "error" for event in events))
        self.assertEqual(capture.stop.call_count, 1)

    def test_stop_during_cue_does_not_restart_listening_or_process_queued_audio(self):
        actions, detector, recognizer, sounds = worker.ActionChannel(), mock.Mock(), mock.Mock(), mock.Mock()
        actions._accept(self.notification("task_progress"))
        actions._accept(self.notification("task_completed", "should-not-play"))
        sounds.play.side_effect = lambda cue: actions._accept({"action": "stop"})
        result, capture, events, reply = self.run_fake_live(actions, detector, recognizer, sounds)
        self.assertEqual(result, 0)
        sounds.play.assert_called_once_with("task_progress")
        self.assertEqual([event["stage"] for event in events], ["started"])
        detector.next_utterance.assert_not_called()
        recognizer.recognize_pcm.assert_not_called()
        self.assertEqual(capture.start.call_count, 1)
        self.assertEqual(capture.stop.call_count, 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
