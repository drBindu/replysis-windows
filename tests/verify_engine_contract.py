#!/usr/bin/env python3
"""
The Windows half of the engine contract.

Everything the app knows about the engine, it learns by matching a string on
stdout. That has one consequence worth stating plainly: RENAMING A LINE IN THE
ENGINE IS INVISIBLE. It compiles, it builds, it passes review, it starts, it
connects - and it produces an app that transcribes nothing, or never answers,
while every signal the engine emits says it is healthy. That is the hardest
failure in this codebase to attribute, and it has now happened three times:
--sysfifo, the single-dash argument convention, and the turn-end signal.

Each was found by running a real session. The argument for this file is that
the check which would have caught them was cheaper than the investigation that
did - seconds against days.

WHAT THIS FILE DOES AND DOES NOT DO

It checks the contract STATICALLY: that every string the C# matches on still
exists in the engine, and that every argument the C# passes is still accepted.
That is exactly the rename class of failure, caught at build time, with no
network, no Speechmatics key, no audio device and no live session.

It does NOT prove the engine works. A line can exist and never be reached; a
config can serialise and be ignored by the server. Only a real utterance proves
that, and the Mac session owns that half because it needs a kernel FIFO and a
real tap - a Mac assertion that only ever runs on Windows would be skipped and
read as coverage, which is the failure this whole effort keeps circling.

Run:  python tests/verify_engine_contract.py
"""

import io
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENGINE = os.path.join(ROOT, "speechmatics_engine.py")
CLIENT = os.path.join(ROOT, "MainWindow.xaml.cs")

# The engine speaks from two files, not one. The provenance banner is printed
# by a PyInstaller runtime hook that build-engine.ps1 generates, because the
# engine source is shared with Mac byte for byte and must not be edited to
# stamp it. That line is engine output and belongs in the contract; it just
# does not live in the engine.
#
# Found by this check failing on its first run, which is the check working.
BUILD_SCRIPT = os.path.join(ROOT, "tools", "build-engine.ps1")

# Fragments the app matches that the engine never contains literally, because
# it builds them with an f-string. Mapped to the fixed part of the format, so
# a change to the format still fails here rather than at an interview.
#
#     engine:  print(f">>> FINAL received ({len(display)} chars)")
#     app:     line.Contains("(0 chars)")
#
# The app is reading a number out of a sentence. That coupling is worth seeing
# written down - it is one reworded log line away from breaking silently.
CONSTRUCTED = {
    "(0 chars)": "chars)",
}

failures = []


def check(ok, label, detail=""):
    print("%s  %s" % ("ok  " if ok else "FAIL", label))
    if not ok:
        failures.append(label)
        if detail:
            print("        %s" % detail)


def read(path):
    return io.open(path, encoding="utf-8-sig", errors="replace").read()


engine = read(ENGINE) + read(BUILD_SCRIPT)
client = read(CLIENT)


def engine_says(phrase):
    """Whether the engine emits this, literally or through a format string."""
    if phrase in engine:
        return True
    fixed = CONSTRUCTED.get(phrase)
    return bool(fixed) and fixed in engine

# ── 1. Every line the client listens for must exist in the engine ────────────
#
# Extracted from the C# rather than listed here. A hand-written list is a
# second copy that drifts, and drift is the thing being guarded against.

listened = set()
for m in re.finditer(r'line\.(?:Contains|StartsWith)\(\s*"([^"]{4,})"', client):
    listened.add(m.group(1))

# Python's own error text, not the engine's vocabulary. The app watches for
# these to explain a missing dependency; they are not lines the engine prints.
NOT_OURS = {"ModuleNotFoundError", "ImportError"}
listened -= NOT_OURS

print("1. Lines the app matches on must exist in the engine")
print("   (%d found in MainWindow.xaml.cs)\n" % len(listened))

for phrase in sorted(listened):
    # The app matches a prefix or fragment; the engine builds the line with an
    # f-string, so compare on the literal fragment.
    check(engine_says(phrase), repr(phrase),
          "no engine line contains this - if it was renamed, the app goes "
          "silent with no error anywhere")

# ── 2. Lines the contract requires, whoever consumes them ────────────────────
#
# Windows ignores UTTERANCE END today and Mac depends on it. It is asserted
# here anyway: the engine is shared, and "Windows does not read this line" is
# not a reason to let it disappear. That reasoning is exactly how --sysfifo
# went missing.

CONTRACTED = {
    ">>> ENGINE BUILD:":   "provenance - which engine is this really",
    "STATUS: ONLINE":      "Windows: readiness, mic pill goes READY",
    "STATUS: OFFLINE":     "Windows: connection lost, clears the ready flag",
    "MIC SIGNAL DETECTED": "Windows: half of the deafness detector",
    "received":            "Windows: the other half - PARTIAL/FINAL received",
    "UTTERANCE END":       "Mac: turn is over. Mac cannot answer without it",
}

print("\n2. Contracted lines, including ones this platform does not read")
for phrase, why in CONTRACTED.items():
    check(engine_says(phrase), "%-22s %s" % (phrase, why))

# ── 3. Every argument the client passes must be accepted ─────────────────────

print("\n3. Arguments the app passes must be accepted by the engine")

passed = set(re.findall(r'"\s*--([a-z-]+)\s', client))
passed |= set(re.findall(r'\$"\s*--([a-z-]+)\s', client))
declared = set(re.findall(r'add_argument\("--([a-z-]+)"', engine))

for arg in sorted(passed):
    check(arg in declared, "--%s" % arg,
          "the app passes this and the engine does not declare it")

# Mac's arguments, asserted from the Windows side for the same reason as above.
for arg in ("sysfifo", "mode", "language", "device", "sysdevice", "max-delay"):
    check(arg in declared, "--%s declared (contract)" % arg)

# ── 4. The engine must actually start and accept them ────────────────────────
#
# --help exercises argparse for real. It costs a second and catches a
# malformed argument definition that a regex over the source cannot see.

print("\n4. argparse accepts the real argument list")
try:
    out = subprocess.run([sys.executable, ENGINE, "--help"],
                         capture_output=True, text=True, timeout=60)
    check(out.returncode == 0, "engine --help exits 0",
          (out.stderr or "")[:300])
    for arg in sorted(passed):
        check("--%s" % arg in out.stdout, "--%s appears in --help" % arg)
except Exception as e:
    check(False, "engine --help runs", str(e))

print("\n" + "=" * 62)
if failures:
    print("%d FAILED" % len(failures))
    for f in failures:
        print("  - %s" % f)
    print("\nA failure here means the app and the engine disagree about what "
          "the engine says.\nThat does not crash. It produces an app that "
          "starts, connects, and does nothing.")
    sys.exit(1)
print("contract holds (static half - a real utterance is the other half)")
