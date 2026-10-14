"""A newer app must never be able to stop an older engine from starting by passing it something it does not know.

2026-10-14: the app began passing --deepgram-only, and a Visual Studio debug build sitting next to an engine copied on 10 October
printed a usage error and exited, so speech never connected at all. The setting travels in the environment now, and an unknown
argument is ignored with a line saying so. This runs the real engine file with an argument nobody has invented yet."""
import os
import subprocess
import sys
import unittest
from pathlib import Path

ENGINE = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"


class UnknownArguments(unittest.TestCase):
    def run_engine(self, *extra, env=None):
        full = dict(os.environ, APP_DATA_DIR=os.path.join(os.environ.get("TEMP", "."), "engine-args-test"))
        full.update(env or {})
        return subprocess.run([sys.executable, str(ENGINE), "--warmup", *extra], capture_output=True, text=True,
                              timeout=60, env=full, cwd=str(ENGINE.parent))

    def test_an_argument_it_has_never_heard_of_is_ignored(self):
        done = self.run_engine("--a-flag-from-the-future", "value")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn("WARMUP OK", done.stdout)
        self.assertIn("Ignoring arguments this engine does not know", done.stdout)
        self.assertIn("--a-flag-from-the-future", done.stdout)

    def test_the_old_flag_still_works_and_nothing_extra_is_said(self):
        done = self.run_engine("--deepgram-only")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertNotIn("Ignoring arguments", done.stdout)

    def test_the_environment_setting_is_harmless_to_start(self):
        done = self.run_engine(env={"REPLYSIS_DEEPGRAM_ONLY": "1"})
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn("WARMUP OK", done.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
