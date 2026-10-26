"""A TV or people in the room must not reach the interviewer's question (owner, 2026-10-25, a Telugu TV in the room)."""
import ast
from pathlib import Path
import struct
import time
import unittest

source = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"
tree = ast.parse(source.read_text(encoding="utf-8-sig"))
constants = {"CALL_SOUND_LEVEL", "MIC_DUCK_AFTER_CALL_S", "MIC_OVER_CALL_LEVEL", "MIC_DUCK_GAIN", "_call_sound_at"}
scope = {"struct": struct, "time": time}
exec(compile(ast.Module(body=[n for n in tree.body
                             if (isinstance(n, ast.Assign) and any(getattr(t, "id", "") in constants for t in n.targets))
                             or (isinstance(n, ast.FunctionDef) and n.name == "duck_mic_for_call")],
                        type_ignores=[]), str(source), "exec"), scope)
duck = scope["duck_mic_for_call"]


def tone(peak: int, n: int = 1600) -> bytes:
    return struct.pack(f"<{n}h", *([peak, -peak] * (n // 2)))


def peak(data: bytes) -> int:
    return max(abs(s) for s in struct.unpack(f"<{len(data) // 2}h", data))


class RoomVoices(unittest.TestCase):
    def setUp(self):
        scope["_call_sound_at"] = 0.0

    def test_room_sound_is_turned_down_while_the_interviewer_speaks_and_just_after(self):
        tv = tone(8000)
        self.assertEqual(peak(duck(tv, 8000, 5000, now=100.0)), 800)   # interviewer speaking
        self.assertEqual(peak(duck(tv, 8000, 0, now=101.0)), 800)      # 1 s after: the quiet that ends the question
        self.assertEqual(peak(duck(tv, 8000, 0, now=102.0)), 8000)     # 2 s after: the microphone is back

    def test_without_a_call_the_microphone_is_untouched(self):
        # An interviewer in the same room is heard only through the microphone: never turned down.
        self.assertEqual(peak(duck(tone(3000), 3000, 0, now=50.0)), 3000)

    def test_a_close_voice_talking_over_the_call_comes_through(self):
        self.assertEqual(peak(duck(tone(12000), 12000, 5000, now=10.0)), 12000)


if __name__ == "__main__":
    unittest.main()
