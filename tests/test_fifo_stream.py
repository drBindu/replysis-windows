"""
Tests for the FIFO system-audio reader in speechmatics_engine.py.

    python tests/test_fifo_stream.py

REQUIRES macOS OR LINUX. Windows has no mkfifo, so these skip there — and a
skipped test that nobody notices is worse than a missing one, because it reads
as coverage. If you are changing the FIFO reader, run this on a machine that
has a kernel FIFO before you merge. Every bug this file exists to catch was
found by running against a real one and would have passed against a simulation.

Three serious bugs have lived in that reader, each introduced by the fix for
the last, and each caught only by someone running it on macOS:

  1. A writer closing left a closed handle in place and every later read raised
     on it — 85,895 errors in fourteen seconds, transcript gone for the session.
  2. The fix answered EAGAIN by returning silence immediately, so the caller
     looped and silence was produced at about 470x realtime, burying real
     speech at 400:1 and returning an empty transcript.
  3. An earlier reattach throttle cost half a second of audio every time the
     writer cycled, which on macOS is every mode switch.

The second is the one to keep in mind when adding tests here. It passed every
correctness test that existed, because "produces silence when quiet" is true at
1x and at 470x alike. THE ASSERTION THAT CATCHES IT IS RATE: feed N seconds of
audio and the reader must consume about N seconds. Anywhere data is synthesised
to fill a gap, assert the rate and not only the content.
"""

import os
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE = os.path.join(os.path.dirname(HERE), "speechmatics_engine.py")

FRAMES = 1600                    # 100ms at 16kHz, the engine's chunk
SAMPLE_RATE = 16000
WANT = FRAMES * 2                # s16le mono

failures = []


def check(label, ok, detail=""):
    print(f"  {'PASS' if ok else 'FAIL'}  {label:44} {detail}")
    if not ok:
        failures.append(label)


def load_fifo_stream():
    """
    Pulls _FifoStream out of the engine and runs it on its own.

    Imported by extraction rather than by importing the engine, because the
    engine parses arguments, opens audio devices and connects to Speechmatics
    at module scope. Extraction keeps the class exactly as it ships — no copy
    to drift out of step with the original.
    """
    source = open(ENGINE, encoding="utf-8").read()
    start = source.index("        class _FifoStream:")
    end = source.index("        try:\n            sys_stream = _FifoStream(args.sysfifo)")
    body = "\n".join(
        line[8:] if line.startswith("        ") else line
        for line in source[start:end].split("\n")
    )
    namespace = {"os": os, "time": time, "SAMPLE_RATE": SAMPLE_RATE}
    exec(compile(body, ENGINE, "exec"), namespace)
    return namespace["_FifoStream"]


def feed(path, seconds, stop):
    """A writer that behaves like the app's tap: 100ms of audio every 100ms."""
    def run():
        try:
            handle = open(path, "wb", buffering=0)
        except Exception:
            return
        end = time.monotonic() + seconds
        while time.monotonic() < end and not stop.is_set():
            try:
                handle.write(b"\x11\x22" * FRAMES)
            except Exception:
                break
            time.sleep(FRAMES / float(SAMPLE_RATE))
        try:
            handle.close()
        except Exception:
            pass

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    return thread


def consumed_seconds(stream, chunks):
    """Wall time, and how much audio the reader claims to have produced."""
    started = time.monotonic()
    produced = sum(len(stream.read(FRAMES)) for _ in range(chunks))
    return time.monotonic() - started, produced / 2 / float(SAMPLE_RATE)


def main():
    if not hasattr(os, "mkfifo"):
        print("SKIPPED — no mkfifo on this platform.")
        print("These tests MUST be run on macOS or Linux before merging any")
        print("change to the FIFO reader in speechmatics_engine.py. Every bug")
        print("they exist to catch was invisible to a simulated FIFO.")
        return 0

    _FifoStream = load_fifo_stream()

    path = os.path.join(
        os.environ.get("TMPDIR", "/tmp"), f"replysis_fifo_test_{os.getpid()}"
    )
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass
    os.mkfifo(path)

    stop = threading.Event()
    try:
        print("FIFO reader")

        stream = _FifoStream(path)

        # ── Rate, with no writer at all ──────────────────────────────────
        # A device delivers 100ms of audio every 100ms whether or not anybody
        # is speaking. So must this, or it floods the recogniser with silence.
        wall, audio = consumed_seconds(stream, 20)
        check("no writer: paced to realtime",
              0.5 <= audio / max(wall, 1e-9) <= 2.0,
              f"{audio:.2f}s audio in {wall:.2f}s wall")

        # ── A writer appears ─────────────────────────────────────────────
        feed(path, 2.0, stop)
        time.sleep(0.3)
        got = stream.read(FRAMES)
        check("writer attached: audio arrives",
              len(got) == WANT and any(got), f"{len(got)} bytes, non-silent")

        # ── Rate while a writer is attached and merely quiet ──────────────
        # This is the case that produced 470x realtime: EAGAIN answered with
        # instant silence, and the caller looping as fast as it could.
        #
        # This one runs FASTER than realtime on purpose — measured at about
        # 1.9x on macOS — and that is correct. It is draining audio the writer
        # had already buffered, and real data must never be held back. Pacing
        # exists to bound synthesised silence, not delivery.
        #
        # Do not "fix" this toward 1.0x. Throttling real reads would add
        # latency to every burst, which is the interviewer's speech arriving
        # late, and it would trade a bug nobody has for one everybody gets.
        # The bound below is deliberately loose enough to allow it.
        wall, audio = consumed_seconds(stream, 20)
        check("attached writer: paced to realtime",
              0.5 <= audio / max(wall, 1e-9) <= 2.0,
              f"{audio:.2f}s audio in {wall:.2f}s wall")

        # ── The writer leaves ────────────────────────────────────────────
        stop.set()
        time.sleep(2.2)
        stop.clear()
        wall, audio = consumed_seconds(stream, 20)
        check("writer gone: paced, no error storm",
              0.5 <= audio / max(wall, 1e-9) <= 2.0,
              f"{audio:.2f}s audio in {wall:.2f}s wall")

        # ── And comes back ───────────────────────────────────────────────
        # A mode switch restarts the app's tap, so this is ordinary, not an
        # edge case. The reader has to reattach without the engine restarting.
        feed(path, 2.0, stop)
        time.sleep(0.4)
        recovered = any(any(stream.read(FRAMES)) for _ in range(15))
        check("writer returns: reattaches", recovered, f"audio flowing again={recovered}")

        # ── Shape ────────────────────────────────────────────────────────
        sizes = {len(stream.read(FRAMES)) for _ in range(10)}
        check("every read is one buffer", sizes == {WANT}, f"sizes={sizes}")

        stream.close()
    finally:
        stop.set()
        try:
            os.unlink(path)
        except OSError:
            pass

    print()
    print("ALL PASS" if not failures else f"{len(failures)} FAILED: {', '.join(failures)}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
