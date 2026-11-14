"""The macOS computer-sound FIFO must answer the mixing loop's get_read_available (Mac, 2026-11-13).

The loop reads the computer's sound only after asking how much is waiting. _FifoStream had no such method, the call
raised, the loop's except turned it into "no audio", and in "both" mode the Mac never heard the interviewer through the
computer, only through the microphone."""
import ast
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

source = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"
tree = ast.parse(source.read_text(encoding="utf-8-sig"))
fifo_class = next(n for n in ast.walk(tree) if isinstance(n, ast.ClassDef) and n.name == "_FifoStream")
scope = {"os": os, "time": time, "SAMPLE_RATE": 16000}
exec(compile(ast.Module(body=[fifo_class], type_ignores=[]), str(source), "exec"), scope)
FifoStream = scope["_FifoStream"]


class FifoStreamAnswersTheLoop(unittest.TestCase):
    def test_the_loop_call_exists(self):
        self.assertTrue(hasattr(FifoStream, "get_read_available"))
        self.assertIn("sys_stream.get_read_available()", source.read_text(encoding="utf-8-sig"))

    @unittest.skipIf(sys.platform == "win32", "FIFOs are the macOS path")
    def test_counts_and_reads_what_is_waiting(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "sys.pcm")
            os.mkfifo(path)
            stream = FifoStream(path)                       # reader first, as the engine does
            writer = os.open(path, os.O_WRONLY | os.O_NONBLOCK)
            try:
                self.assertEqual(stream.get_read_available(), 0)
                audio = bytes(range(256)) * 12 + bytes(128)  # 3200 bytes, 1600 frames
                os.write(writer, audio)
                self.assertEqual(stream.get_read_available(), 1600)
                self.assertEqual(stream.read(1600), audio)
                self.assertEqual(stream.get_read_available(), 0)
                os.write(writer, audio[:640])
                n = stream.get_read_available()
                self.assertEqual(n, 320)
                self.assertEqual(stream.read(min(n, 1600)), audio[:640])
            finally:
                os.close(writer)
                stream.close()

    @unittest.skipIf(sys.platform == "win32", "FIFOs are the macOS path")
    def test_no_writer_yet_is_zero_not_an_error(self):
        with tempfile.TemporaryDirectory() as d:
            stream = FifoStream(os.path.join(d, "missing.pcm"))
            self.assertEqual(stream.get_read_available(), 0)


if __name__ == "__main__":
    unittest.main()
