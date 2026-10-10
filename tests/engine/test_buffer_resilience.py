"""
A question asked while the speech connection is stalling or being replaced must not be lost, and stale audio must not come back.

Found on 2026-10-10 in the real installed app: the first spoken question got no transcript. The speech provider closed the
connection after ten seconds without data, and the audio waiting to be sent was thrown away with the connection.

The code under test is taken out of the real engine source and run here, so this checks what ships.
Run: python tests/engine/test_buffer_resilience.py
"""
import asyncio
import os
import tempfile
import threading
import time
import unittest
from collections import deque

SOURCE = os.path.join(os.path.dirname(__file__), "..", "..", "speechmatics_engine.py")


def load():
    text = open(SOURCE, encoding="utf-8-sig").read()
    start = text.index("class BufferedMixedStream:")
    end = text.index("async def run_deepgram() -> str:")
    ns = {
        "os": os, "time": time, "threading": threading, "deque": deque, "asyncio": asyncio,
        "CHUNK_FRAMES": 1600,
        "PAUSE_FLAG": os.path.join(tempfile.gettempdir(), "replysis-test-no-such-pause-flag"),
    }
    exec(compile(text[start:end], "engine_buffer", "exec"), ns)
    return ns


NS = load()
Buffered = NS["BufferedMixedStream"]
SendStalled = NS["SendStalled"]
send_with_guard = NS["_send_with_stall_guard"]


class ScriptedSource:
    """Hands out chunks on a schedule: (seconds to wait first, bytes). Blocks forever when the script is finished."""

    def __init__(self, script):
        self.script = list(script)
        self.done = threading.Event()

    def read(self, frames, exception_on_overflow=False):
        if not self.script:
            self.done.set()
            time.sleep(60)
            return b""
        delay, data = self.script.pop(0)
        time.sleep(delay)
        return data


def wait_for(cond, timeout=5.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if cond():
            return True
        time.sleep(0.01)
    return False


class BufferResilienceTests(unittest.TestCase):
    def test_audio_recorded_but_not_sent_survives_a_reconnect(self):
        chunks = [(0.002, b"c%03d" % i) for i in range(60)]
        buf = Buffered(ScriptedSource(chunks))
        try:
            self.assertTrue(wait_for(lambda: len(buf._chunks) >= 60), "all sixty chunks were captured")
            dropped = buf.drop_stale()
            self.assertEqual(dropped, 0, "six seconds of unsent audio is kept on a reconnect (it used to keep 1.5 s)")
            self.assertEqual(buf.read_timeout(0.1), b"c000", "and it is sent oldest first")
        finally:
            buf.close()

    def test_more_than_ten_seconds_is_trimmed_to_the_newest(self):
        chunks = [(0.001, b"c%03d" % i) for i in range(120)]
        buf = Buffered(ScriptedSource(chunks))
        try:
            self.assertTrue(wait_for(lambda: len(buf._chunks) >= 120))
            dropped = buf.drop_stale()
            self.assertEqual(dropped, 20, "keeps the newest ten seconds, drops the older twenty chunks")
            self.assertEqual(buf.read_timeout(0.1), b"c020")
        finally:
            buf.close()

    def test_audio_from_before_a_gap_is_never_replayed(self):
        # a laptop that slept, or a capture that stalled: what was buffered before the gap is stale
        class QuickGap(Buffered):
            CAPTURE_GAP_SECONDS = 0.25
        chunks = [(0.002, b"old%d" % i) for i in range(5)] + [(0.6, b"new0")] + [(0.002, b"new%d" % i) for i in range(1, 4)]
        src = ScriptedSource(chunks)
        buf = QuickGap(src)
        try:
            self.assertTrue(src.done.wait(5), "the whole script was read")
            got = []
            while True:
                c = buf.read_timeout(0.05)
                if c is None:
                    break
                got.append(c)
            self.assertEqual(got, [b"new0", b"new1", b"new2", b"new3"], "nothing from before the gap comes back")
        finally:
            buf.close()

    def test_a_chunk_that_could_not_be_sent_goes_back_to_the_front(self):
        buf = Buffered(ScriptedSource([(0.001, b"b"), (0.001, b"c")]))
        try:
            self.assertTrue(wait_for(lambda: len(buf._chunks) >= 2))
            first = buf.read_timeout(0.1)
            self.assertEqual(first, b"b")
            buf.push_front(first)
            self.assertEqual(buf.read_timeout(0.1), b"b", "put back, it is the next one sent")
            self.assertEqual(buf.read_timeout(0.1), b"c")
        finally:
            buf.close()


class StalledSendTests(unittest.TestCase):
    def test_a_send_that_never_completes_raises_and_keeps_the_audio(self):
        class StuckSocket:
            async def send(self, data):
                await asyncio.sleep(30)

        buf = Buffered(ScriptedSource([]))
        try:
            started = time.monotonic()
            with self.assertRaises(SendStalled):
                asyncio.run(send_with_guard(StuckSocket(), b"question-audio", buf, timeout=0.2))
            self.assertLess(time.monotonic() - started, 2.0, "noticed in a fraction of the provider's ten second wait")
            self.assertEqual(buf.read_timeout(0.1), b"question-audio", "the chunk is waiting for the next connection")
        finally:
            buf.close()

    def test_a_normal_send_just_goes(self):
        sent = []

        class GoodSocket:
            async def send(self, data):
                sent.append(data)

        buf = Buffered(ScriptedSource([]))
        try:
            asyncio.run(send_with_guard(GoodSocket(), b"hello", buf, timeout=1.0))
            self.assertEqual(sent, [b"hello"])
            self.assertIsNone(buf.read_timeout(0.05), "nothing was put back")
        finally:
            buf.close()

    def test_a_stalled_send_is_a_connection_error_not_a_no_network_one(self):
        # the reconnect handler treats timeouts and unreachable-host errors as "no network, wait quietly"; a stalled
        # upload on a live connection must go the ordinary reconnect route instead
        self.assertTrue(issubclass(SendStalled, ConnectionError))
        e = SendStalled("audio upload stalled for 3s")
        self.assertIsNone(getattr(e, "winerror", None))
        self.assertIsNone(e.errno)


if __name__ == "__main__":
    unittest.main(verbosity=2)
