"""The weak line audio: 16 bit PCM to 8 bit mu-law (G.711) is correct, half the size, and only used when the app asks for it."""
import ast
import os
import struct
import tempfile
import time
import unittest
import urllib.parse
from pathlib import Path
from types import SimpleNamespace

source = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"
tree = ast.parse(source.read_text(encoding="utf-8"))
func_names = {"_build_mulaw_table", "_pcm16_to_mulaw", "_slow_line_requested", "_deepgram_url", "_deepgram_keyterms", "_deepgram_flux"}
const_names = {"_MULAW_TABLE", "_SLOW_LINE_FLAG", "_SLOW_LINE_FRESH_SECONDS", "DEEPGRAM_URL", "DEEPGRAM_MAX_KEYTERMS",
               "_DEEPGRAM_BUILTIN_TERMS", "SAMPLE_RATE"}
scope = {"struct": struct, "os": os, "time": time, "urllib": urllib, "args": SimpleNamespace(utterance_silence=1.0),
         "SAMPLE_RATE": 16000, "DEEPGRAM_URL": "wss://api.deepgram.com/v1/listen", "DEEPGRAM_MAX_KEYTERMS": 100,
         "_DEEPGRAM_BUILTIN_TERMS": ["Replysis"], "_load_extra_vocab": lambda: [], "APP_DATA": tempfile.gettempdir()}
for node in tree.body:
    if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in const_names for t in node.targets):
        exec(compile(ast.Module(body=[node], type_ignores=[]), str(source), "exec"), scope)
exec(compile(ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in func_names],
                        type_ignores=[]), str(source), "exec"), scope)


def mulaw_decode(byte: int) -> int:
    """The standard G.711 decoder, to check the encoder's round trip against."""
    byte = ~byte & 0xFF
    sign, exponent, mantissa = byte & 0x80, (byte >> 4) & 0x07, byte & 0x0F
    sample = ((mantissa << 3) + 0x84) << exponent
    sample -= 0x84
    return -sample if sign else sample


class WeakLineAudio(unittest.TestCase):
    def test_known_values_of_the_standard(self):
        enc = scope["_pcm16_to_mulaw"]
        self.assertEqual(enc(struct.pack("<h", 0)), b"\xff")          # silence
        self.assertEqual(enc(struct.pack("<h", 32767)), b"\x80")      # loudest positive
        self.assertEqual(enc(struct.pack("<h", -32768)), b"\x00")     # loudest negative

    def test_half_the_bytes(self):
        pcm = struct.pack("<1600h", *range(-800, 800))
        self.assertEqual(len(scope["_pcm16_to_mulaw"](pcm)), 1600)
        self.assertEqual(len(pcm), 3200)

    def test_round_trip_error_stays_small_for_speech_levels(self):
        enc = scope["_pcm16_to_mulaw"]
        worst = 0.0
        for s in range(-30000, 30001, 37):
            back = mulaw_decode(enc(struct.pack("<h", s))[0])
            if abs(s) > 100:
                worst = max(worst, abs(back - s) / abs(s))
        self.assertLess(worst, 0.07, "mu-law keeps every sample within about 6 percent, the same as a phone call")

    def test_url_asks_for_the_smaller_format_only_on_a_weak_line(self):
        url = scope["_deepgram_url"]
        normal = urllib.parse.parse_qs(urllib.parse.urlsplit(url()).query)
        slow = urllib.parse.parse_qs(urllib.parse.urlsplit(url(True)).query)
        self.assertEqual(normal["encoding"], ["linear16"])
        self.assertEqual(slow["encoding"], ["mulaw"])
        self.assertEqual(slow["sample_rate"], ["16000"])     # the same rate, so the words are the same

    def test_flag_is_read_and_goes_stale(self):
        d = tempfile.mkdtemp()
        scope["APP_DATA"] = d
        flag = os.path.join(d, "slowline.flag")
        self.assertFalse(scope["_slow_line_requested"]())
        Path(flag).write_text("now")
        self.assertTrue(scope["_slow_line_requested"]())
        old = time.time() - 16 * 60
        os.utime(flag, (old, old))
        self.assertFalse(scope["_slow_line_requested"](), "a flag from more than 15 minutes ago is ignored")


if __name__ == "__main__":
    unittest.main(verbosity=1)
