"""Execute the shipping recording routines without opening audio or a network."""
import ast
import os
from pathlib import Path
import tempfile
import threading
import unittest
import wave
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[1] / "speechmatics_engine.py"
TREE = ast.parse(SOURCE.read_text(encoding="utf-8"))


class RecordingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.ns = dict(os=os, threading=threading, wave=wave,
                       APP_DATA=self.temp.name, RECORDINGS_DIR=self.temp.name,
                       record_lock=threading.Lock(), recording_frames=[b'\0' * 3200],
                       _recording_ids_marked=set(), get_recording_session_number=lambda: 1)
        names = {"MAX_RECORDING_SECONDS", "CHUNK_FRAMES", "SAMPLE_RATE", "MAX_RECORDING_FRAMES"}
        functions = {"mark_recording_saved", "save_recording"}
        nodes = [n for n in TREE.body if
                 (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in names for t in n.targets))
                 or (isinstance(n, ast.FunctionDef) and n.name in functions)]
        exec(compile(ast.Module(body=nodes, type_ignores=[]), str(SOURCE), "exec"), self.ns)

    def test_ninety_minutes_at_current_chunk_size(self):
        n = self.ns
        seconds = n['MAX_RECORDING_FRAMES'] * n['CHUNK_FRAMES'] / n['SAMPLE_RATE']
        self.assertEqual(seconds, 90 * 60)
        # A full hour must fit, without allocating an hour of audio in this test.
        self.assertGreaterEqual(n['MAX_RECORDING_FRAMES'], 60 * 60 * n['SAMPLE_RATE'] // n['CHUNK_FRAMES'])

    def test_success_wav_is_closed_and_readable_before_marker(self):
        original = self.ns['mark_recording_saved']
        def marked(recording_id):
            with wave.open(os.path.join(self.temp.name, 'interview_1.wav'), 'rb') as wav:
                self.assertEqual(wav.getnframes(), 1600)
            original(recording_id)
        self.ns['mark_recording_saved'] = marked
        self.ns['save_recording']('test')
        self.assertTrue(Path(self.temp.name, 'recording_saved_test.flag').exists())
        self.assertFalse(Path(self.temp.name, 'recording_failed_test.flag').exists())

    def test_open_failure_never_reports_success(self):
        with patch.object(wave, 'open', side_effect=OSError('disk full')):
            self.ns['save_recording']('test')
        self.assertFalse(Path(self.temp.name, 'recording_saved_test.flag').exists())
        self.assertTrue(Path(self.temp.name, 'recording_failed_test.flag').exists())

    def test_write_and_close_failure_never_report_success(self):
        for method in ('writeframes', 'close'):
            with self.subTest(method=method):
                self.ns['recording_frames'] = [b'\0' * 3200]
                with patch.object(wave, 'open') as opened:
                    getattr(opened.return_value, method).side_effect = OSError('disk full')
                    self.ns['save_recording'](method)
                self.assertFalse(Path(self.temp.name, f'recording_saved_{method}.flag').exists())
                self.assertTrue(Path(self.temp.name, f'recording_failed_{method}.flag').exists())


if __name__ == '__main__':
    unittest.main()
