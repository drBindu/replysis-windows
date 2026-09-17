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
# -- Recording saved marker ---------------------------------------------------
# The app protects a recording once this marker exists. Written before the
# background save closes the file, it protected half a recording and left the
# raw WAV behind. Written on every cycle, it cost 36,000 file writes an hour.
_idle_calls = re.findall(r"if not stop_recording\(shutdown_requested\):\s*\n\s*(\w+)\(", SOURCE)
check("audio loop never marks a recording saved",
      len(_idle_calls) == 2 and all(c == "mark_nothing_recorded" for c in _idle_calls),
      f"calls after stop_recording: {_idle_calls}")
_nothing = re.search(r"def mark_nothing_recorded\(recording_id\):(.*?)\n\n\n", SOURCE, re.S)
check("empty-session marker skips started recordings",
      _nothing is not None and "_recording_ids_started" in _nothing.group(1)
      and "_recording_ids_marked" in _nothing.group(1),
      "writes once, never for a recording still saving")
check("recording start is remembered",
      "_recording_ids_started.add(active_recording_id)" in SOURCE)
_save = re.search(r"def save_recording\(recording_id\):(.*?)\n\n\n", SOURCE, re.S)
check("save_recording marks only after closing the file",
      _save is not None and _save.group(1).find("wf.close()") < _save.group(1).find("mark_recording_saved("),
      "marker follows close")

# -- Microphone static ---------------------------------------------------------
# Every DirectSound input at 16 kHz returned white noise at full scale on a real
# machine. Chosen as "the loudest microphone", it drowned the interviewer too.
check("static from a microphone is detected", "def _is_capture_noise" in SOURCE)
check("DirectSound inputs are never offered as a switch",
      "directsound" in SOURCE[SOURCE.index("def _real_input_devices"):SOURCE.index("def _find_a_microphone_that_hears")])
check("static is never mixed into the audio",
      "if _is_capture_noise(mic_data):" in SOURCE and "mic_data = SILENCE" in SOURCE)
check("the microphone search can run more than once", "MIC_AUTOSWITCH_MAX_ATTEMPTS = 3" in SOURCE)
import struct as _struct
_ns = {"struct": _struct}
exec(SOURCE[SOURCE.index("def _is_capture_noise"):SOURCE.index("def _signal_level")], _ns)
_noise = _struct.pack("<1600h", *([32767, -32768] * 800))
_voice = _struct.pack("<1600h", *[int(9000 * ((i % 40) - 20) / 20) for i in range(1600)])
_loud = _struct.pack("<1600h", *([32767] * 30 + [1000] * 1570))
check("white noise at full scale counts as static", _ns["_is_capture_noise"](_noise))
check("a normal voice is not static", not _ns["_is_capture_noise"](_voice))
check("a voice clipping briefly is not static", not _ns["_is_capture_noise"](_loud))

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

# ── Deepgram ─────────────────────────────────────────────────────────────────
# Deepgram runs first for English and Speechmatics is its fallback. The fallback
# is the promise: a Deepgram path that cannot hand over turns a refused token or
# a full account into silence, where before it was only slower transcription.
check("Deepgram hands over to Speechmatics",
      '"fallback"' in SOURCE and "Speechmatics is taking over" in SOURCE)
check("Deepgram refusals are not retried",
      re.search(r"_DEEPGRAM_REFUSED\s*=\s*\{[^}]*401[^}]*402[^}]*403[^}]*429", SOURCE) is not None,
      "401 402 403 429 go straight to Speechmatics")
check("Deepgram only with a token", 'os.environ.get("DG_TOKEN"' in SOURCE,
      "the Mac passes no DG_TOKEN and must stay on Speechmatics")

# Without keyterms nova-3 wrote "Kubernets", "Readys" and "Next dot j s" in the
# comparison that chose it. The speed only counted because keyterms fixed that.
check("Deepgram sends keyterms", '("keyterm", term)' in SOURCE)

# Both apps read these lines. Windows latches online from STATUS: ONLINE and the
# Mac answers only after UTTERANCE END, so a path that prints neither transcribes
# into an app that never shows it as connected or never answers.
dg = SOURCE[SOURCE.find("async def run_deepgram"):SOURCE.find("# ── MAIN WITH AUTO-RECONNECT")]
check("Deepgram path reports STATUS: ONLINE", "STATUS: ONLINE" in dg)
check("Deepgram path reports UTTERANCE END", "UTTERANCE END" in dg)
check("Deepgram path honours reset.flag", "RESET_FLAG" in dg)

# A server that accepts and closes at once was reconnected in a tight loop
# forever, because every successful handshake reset the failure count.
check("Deepgram instant closes count as failures",
      "_DEEPGRAM_HEALTHY_SECONDS" in dg and "sessions closed at once" in dg)

print()
print("ALL PASS" if not failures else f"{len(failures)} FAILED: {', '.join(failures)}")
sys.exit(1 if failures else 0)
