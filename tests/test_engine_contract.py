"""
Tests for the parts of speechmatics_engine.py that both apps depend on.

    python tests/test_engine_contract.py

Runs anywhere. Nothing here needs audio hardware, a network, or a FIFO — it
reads the engine source and checks the contract the Mac and Windows apps are
each built against. The FIFO reader itself is tested separately in
test_fifo_stream.py, which needs a kernel FIFO and skips on Windows.

This file exists because the two apps drifted apart without either side being
able to tell. The Mac shipped a compiled build of 1,074 lines against a source
of 1,982, from before the Sarvam language path and before additional_vocab, and
a user speaking Telugu into it got confident English nonsense answered as real
questions. Nothing detected that for months.

Each check below is a promise one of the apps relies on. If a check fails, some
build somewhere stops transcribing, and — this is the part that makes it worth
testing — it will most likely stop silently.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE = os.path.join(os.path.dirname(HERE), "speechmatics_engine.py")
SOURCE = open(ENGINE, encoding="utf-8").read()

failures = []


def check(label, ok, detail=""):
    print(f"  {'PASS' if ok else 'FAIL'}  {label:52} {detail}")
    if not ok:
        failures.append(label)


print("Engine contract")

# ── Arguments ────────────────────────────────────────────────────────────────
# Windows declares long options with two dashes; the Mac passes one. argparse
# rejects the single-dash form outright, so a shared build refuses to start on
# the Mac and looks broken rather than misconfigured.
check("single-dash long options are normalised",
      "_accept_single_dash" in SOURCE,
      "Mac passes -mode, Windows declares --mode")

# The Mac falls back to microphone-only when its CoreAudio tap is refused
# permission or crashes mid-session. Removing this mode turns a
# degraded-but-working fallback into an engine that will not start.
modes = re.search(r'choices=\[([^\]]*)\]', SOURCE)
check("--mode offers both, system and mic",
      modes is not None and all(m in modes.group(1) for m in ('"both"', '"system"', '"mic"')),
      modes.group(1) if modes else "not found")

# macOS cannot capture system audio in a helper process: it does not inherit
# the app's screen-recording grant, so the OS returns silence rather than an
# error. The Mac runs its own tap and writes to a FIFO instead.
check("--sysfifo exists", "--sysfifo" in SOURCE, "macOS has no other route to system audio")

# ── Environment ──────────────────────────────────────────────────────────────
# Windows sets SM_API_KEY, macOS sets SPEECHMATICS_API_KEY. Two names for one
# thing, and renaming either breaks the other side for nothing.
check("both key variable names accepted",
      "SM_API_KEY" in SOURCE and "SPEECHMATICS_API_KEY" in SOURCE)

# LOCALAPPDATA is Windows-only. Without APP_DATA_DIR the engine writes
# latest.txt, pause.flag and reset.flag to a temp directory on macOS while the
# app polls Application Support: the engine runs perfectly, the app shows an
# empty transcript forever, and there is no error at either end.
check("APP_DATA_DIR is honoured",
      "APP_DATA_DIR" in SOURCE,
      "otherwise macOS writes where the app is not looking")
check("LOCALAPPDATA remains the fallback", "LOCALAPPDATA" in SOURCE)

# ── Terminal conditions ──────────────────────────────────────────────────────
# The default here has cost twice. A blocked contract fell through to "trying
# next endpoint" and retried forever behind a UI saying "connecting"; that was
# fixed by adding the string. Then quota_exceeded — reachable by having both
# apps open at once — arrived through a different string and did the same.
#
# Enumerating conditions one at a time loses that race by construction, so the
# default is terminal and the transient cases are the enumerated ones. If this
# check fails, some unmet server refusal is once again an infinite retry.
lowered = SOURCE.lower()
check("session refusals fail closed",
      all(term in lowered for term in ("not_allowed", "quota", "forbidden")),
      "unenumerated refusals must not retry forever")
check("blocked contract is terminal", "contract blocked" in lowered)
check("audio usage limit is terminal", "audio usage exceeded" in lowered)

# ── Language ─────────────────────────────────────────────────────────────────
# Speechmatics cannot do Telugu, so those languages route to Sarvam. Note that
# SARVAM_API_KEY is empty on every install of both apps today and there is no
# UI to set one, so this path has never run in production. The routing existing
# is still worth protecting: it is what a key would switch on.
check("Sarvam languages route away from Speechmatics", "SARVAM_LANG_MAP" in SOURCE)

# melia-1 validates against a different schema and rejects additional_vocab,
# punctuation_overrides, enable_entities, max_delay and max_delay_mode.
# Requesting it took transcription down completely, on every endpoint, on every
# retry, and the downgrade guard did not fire because it matched "not_allowed"
# while the API says "is not allowed".
check("model rejection is detected and downgraded",
      "_downgrade_model_if_rejected" in SOURCE)

# ── Timing ───────────────────────────────────────────────────────────────────
# The Speechmatics realtime API refuses anything below 0.7 with a
# protocol_error, and this is the single largest delay between a question
# ending and an answer appearing.
delay = re.search(r'"--max-delay",\s*type=float,\s*default=([0-9.]+)', SOURCE)
check("max-delay is at the API floor",
      delay is not None and float(delay.group(1)) >= 0.7,
      f"default={delay.group(1) if delay else '?'} (API rejects below 0.7)")

print()
print("ALL PASS" if not failures else f"{len(failures)} FAILED: {', '.join(failures)}")
sys.exit(1 if failures else 0)
