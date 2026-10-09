"""
The engine's timed-read helper must never run two reads on one audio stream at once.

WASAPI loopback blocks for as long as nothing is playing. The helper used to give up after its timeout and start another read on the
same stream, so every quiet stretch piled up reads inside PortAudio, and Windows' audio library crashed the engine (access violation in
AUDIOSES.DLL, heap corruption in ntdll), found by the overnight test on 2026-10-09.

The helper is taken out of the real engine source and run here, so this checks what ships. Run: python tests/engine/test_read_helper.py
"""
import os
import sys
import threading
import time
import unittest

SOURCE = os.path.join(os.path.dirname(__file__), "..", "..", "speechmatics_engine.py")


def load_helper():
    text = open(SOURCE, encoding="utf-8-sig").read()
    start = text.index("_timeout_warned = set()")
    end = text.index("def open_stream_with_timeout(")
    namespace = {"threading": threading}
    exec(compile(text[start:end], "engine_read_helper", "exec"), namespace)
    return namespace["_read_stream_timeout"]


class BlockingStream:
    """Blocks in read() until released, like a silent WASAPI loopback, and records how many reads are inside at once."""

    def __init__(self):
        self.release = threading.Event()
        self.lock = threading.Lock()
        self.inside = 0
        self.most_inside = 0
        self.calls = 0

    def read(self, frames, exception_on_overflow=False):
        with self.lock:
            self.calls += 1
            self.inside += 1
            self.most_inside = max(self.most_inside, self.inside)
        try:
            self.release.wait(10)
            return b"audio-%d" % frames
        finally:
            with self.lock:
                self.inside -= 1


class ReadHelperTests(unittest.TestCase):
    def setUp(self):
        self.read = load_helper()

    def test_a_silent_device_never_gets_a_second_read_started(self):
        stream = BlockingStream()
        for _ in range(60):                       # 60 quiet chunks in a row
            self.assertIsNone(self.read(stream, 100, 0.005, "SYS"))
        self.assertEqual(stream.calls, 1, "one read, waited on again, not replaced")
        self.assertEqual(stream.most_inside, 1, "never two reads on one stream at once")
        stream.release.set()

    def test_the_waiting_read_delivers_its_audio_when_sound_arrives(self):
        stream = BlockingStream()
        self.assertIsNone(self.read(stream, 100, 0.005, "SYS"))
        stream.release.set()
        got = None
        for _ in range(100):
            got = self.read(stream, 100, 0.05, "SYS")
            if got is not None:
                break
        self.assertEqual(got, b"audio-100", "the audio the blocked read was waiting for is returned, not thrown away")

    def test_after_a_read_finishes_the_next_call_starts_a_new_one(self):
        stream = BlockingStream()
        stream.release.set()
        self.assertEqual(self.read(stream, 10, 1.0, "SYS"), b"audio-10")
        self.assertEqual(self.read(stream, 20, 1.0, "SYS"), b"audio-20")
        self.assertEqual(stream.calls, 2)
        self.assertEqual(stream.most_inside, 1)

    def test_two_different_streams_are_read_independently(self):
        a, b = BlockingStream(), BlockingStream()
        b.release.set()
        self.assertIsNone(self.read(a, 10, 0.005, "SYS"))
        self.assertEqual(self.read(b, 10, 1.0, "MIC"), b"audio-10", "a stuck stream does not hold up another")
        a.release.set()

    def test_a_read_that_raises_is_reported_as_nothing_not_a_crash(self):
        class Broken:
            def read(self, frames, exception_on_overflow=False):
                raise OSError("device gone")
        self.assertIsNone(self.read(Broken(), 10, 1.0, "SYS"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
