"""Execute shipping PCM helpers without opening devices or contacting providers."""
import ast
import math
from pathlib import Path
import struct
import unittest

source = Path(__file__).resolve().parents[1] / "speechmatics_engine.py"
tree = ast.parse(source.read_text(encoding="utf-8"))
names = {"resample_to_16k_mono", "mix_audio", "_signal_level", "_mic_needs_recovery"}
scope = {"struct": struct}
constants = {"MIC_DEAD_READS_BEFORE_RESEARCH", "MIC_QUIET_READS_BEFORE_SWITCH", "MIC_DEAD_LEVEL"}
for node in tree.body:
    if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in constants for t in node.targets):
        exec(compile(ast.Module(body=[node], type_ignores=[]), str(source), "exec"), scope)
exec(compile(ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names],
                        type_ignores=[]), str(source), "exec"), scope)


class AudioSamples(unittest.TestCase):
    def test_quiet_room_does_not_trigger_device_scans(self):
        for ever_heard in (False, True):
            dead_reads = 0
            for amplitude in [32, 150, 399, 9] * 150:
                dead_reads = dead_reads + 1 if amplitude <= scope["MIC_DEAD_LEVEL"] else 0
                self.assertFalse(scope["_mic_needs_recovery"](ever_heard, dead_reads))

    def test_dead_route_recovers_after_bounded_silence(self):
        for ever_heard, threshold in ((False, 20), (True, 40)):
            self.assertFalse(scope["_mic_needs_recovery"](ever_heard, threshold - 1))
            self.assertTrue(scope["_mic_needs_recovery"](ever_heard, threshold))

    def test_short_loopback_packets_keep_pitch_and_pad_silence(self):
        for rate in (24000, 44100, 48000, 96000):
            for channels in (1, 2, 8):
                count = rate // 50  # 20 ms of a 1 kHz tone, not 100 ms
                samples = [int(8000 * math.sin(2 * math.pi * 1000 * i / rate))
                           for i in range(count) for _ in range(channels)]
                raw = struct.pack(f"<{len(samples)}h", *samples)
                out = scope["resample_to_16k_mono"](raw, rate, channels, 1600)
                values = struct.unpack("<1600h", out)
                self.assertTrue(all(v == 0 for v in values[321:]), (rate, channels))
                crossings = sum(a < 0 <= b for a, b in zip(values[:319], values[1:320]))
                self.assertTrue(18 <= crossings <= 21, (rate, channels, crossings))

    def test_empty_packet_is_silence(self):
        for rate in (8000, 16000, 48000):
            self.assertEqual(scope["resample_to_16k_mono"](b"", rate, 2, 1600), bytes(3200))

    def test_silent_loopback_preserves_microphone(self):
        voice = struct.pack("<1600h", *([900, -900] * 800))
        self.assertEqual(scope["mix_audio"](voice, bytes(3200)), voice)


if __name__ == "__main__":
    unittest.main()
