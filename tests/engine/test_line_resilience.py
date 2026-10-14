"""A dropped line is retried on Deepgram, a dead connection is noticed in about four seconds, and the audio that went into it is put
back. Drives the engine's own Deepgram loop (extracted unchanged from speechmatics_engine.py) against a local websocket server that
answers like Deepgram, goes silent like a dead connection, or never answers at all. No network, no credentials, no microphone.

Needs the websockets package, like the engine itself; skipped where it is not installed."""
import ast
import asyncio
import json
import os
import socket
import struct
import tempfile
import time
import unittest
from collections import deque
from pathlib import Path

try:
    import websockets
except ImportError:           # the CI runner has no third party packages; the engine tests run on a developer machine
    websockets = None

SOURCE = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"
TREE = ast.parse(SOURCE.read_text(encoding="utf-8"))
FUNCS = {"_run_deepgram_loop", "_send_with_stall_guard", "_slow_line_requested", "_pcm16_to_mulaw", "_build_mulaw_table",
         "_http_status", "_looks_like_no_network", "_network_is_down", "_is_line_problem", "_deepgram_only", "_deepgram_flux"}
CLASSES = {"SendStalled", "DeadConnection"}
CONSTS = {"_MULAW_TABLE", "_SLOW_LINE_FLAG", "_SLOW_LINE_FRESH_SECONDS", "_DEEPGRAM_REFUSED", "_DEEPGRAM_MAX_FAILURES",
          "_DEEPGRAM_OPEN_SECONDS", "_DEEPGRAM_HEALTHY_SECONDS", "_DEEPGRAM_SILENT_SECONDS", "_DEEPGRAM_LINE_PROBLEMS_HEARD",
          "_DEEPGRAM_LINE_PROBLEMS_NEVER_HEARD", "_DEEPGRAM_REPLAY_CHUNKS", "_DEEPGRAM_REACH_EVERY",
          "_DEEPGRAM_REACH_UP_TO", "_DEEPGRAM_PING_EVERY", "_DEEPGRAM_PING_ALLOWED", "_DEEPGRAM_FIRST_ANSWER_SECONDS",
          "_DEEPGRAM_FIRST_ANSWER_WINDOW"}


def engine_scope(app, url, buffered_class, printed, deepgram_only=False):
    scope = {"asyncio": asyncio, "json": json, "os": os, "time": time, "struct": struct, "socket": socket, "deque": deque,
             "args": type("A", (), {"deepgram_only": deepgram_only})(),
             "APP_DATA": app, "SHUTDOWN_FLAG": os.path.join(app, "shutdown.flag"), "PAUSE_FLAG": os.path.join(app, "pause.flag"),
             "RESET_FLAG": os.path.join(app, "reset.flag"), "_DG_TOKEN": "test", "BufferedMixedStream": buffered_class,
             "MixedStream": lambda: None, "_write_latest": lambda text: None,
             "_deepgram_url": lambda slow_line=False: url,
             "_deepgram_host_reachable": lambda timeout=1.5: True,   # the local server is always "reachable"
             "print": lambda *a, **k: printed.append(" ".join(str(x) for x in a))}
    for node in TREE.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in CONSTS for t in node.targets):
            exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), "exec"), scope)
    for node in TREE.body:
        if isinstance(node, ast.ClassDef) and node.name in CLASSES:
            exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), "exec"), scope)
    for node in TREE.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in FUNCS:
            exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), "exec"), scope)
    return scope


class FakeBuffered:
    """Audio at 100 ms a chunk in real time, as the capture thread hands it over. Remembers what was put back."""
    instances = []

    def __init__(self, source):
        self.pushed = []
        self.put_back = 0
        self.stop_at = None
        self.app = None
        FakeBuffered.instances.append(self)

    def read_timeout(self, timeout):
        time.sleep(0.1)
        if self.stop_at is not None and time.monotonic() > self.stop_at:
            Path(self.app, "shutdown.flag").write_text("x")
            raise RuntimeError("test finished")
        if self.pushed:
            return self.pushed.pop()
        return bytes(3200)

    def push_front(self, chunk):
        self.pushed.append(chunk)
        self.put_back += 1

    def drop_stale(self):
        pass


@unittest.skipIf(websockets is None, "websockets is not installed")
class LineResilience(unittest.TestCase):
    def run_loop(self, behaviour, seconds, deepgram_only=False):
        """Runs the engine's loop against a local server for `seconds`, then asks it to stop. Returns what it printed, what it
        returned, and how many connections and audio bytes the server saw."""
        printed, seen = [], {"connections": 0, "bytes": {}, "started": {}}

        async def handler(ws):
            index = seen["connections"]
            seen["connections"] += 1
            seen["bytes"][index] = 0
            seen["started"][index] = time.monotonic()

            async def talk():
                started = time.monotonic()
                while True:
                    await asyncio.sleep(0.3)
                    if behaviour(index, time.monotonic() - started):
                        await ws.send(json.dumps({"type": "Results", "is_final": False,
                                                  "channel": {"alternatives": [{"transcript": ""}]}}))
            talker = asyncio.create_task(talk())
            try:
                async for message in ws:
                    if isinstance(message, bytes):
                        seen["bytes"][index] += len(message)
            except Exception:
                pass
            finally:
                talker.cancel()

        async def main():
            app = tempfile.mkdtemp(prefix="line-")
            async with websockets.serve(handler, "127.0.0.1", 0) as server:
                port = server.sockets[0].getsockname()[1]
                FakeBuffered.instances.clear()
                scope = engine_scope(app, f"ws://127.0.0.1:{port}/", FakeBuffered, printed, deepgram_only)
                runner = asyncio.create_task(scope["_run_deepgram_loop"]({}))
                await asyncio.sleep(0.5)
                buffered = FakeBuffered.instances[0] if FakeBuffered.instances else None
                if buffered is not None:
                    buffered.app = app
                    buffered.stop_at = time.monotonic() + seconds
                result = await asyncio.wait_for(runner, seconds + 40)
                return result

        result = asyncio.run(main())
        put_back = FakeBuffered.instances[0].put_back if FakeBuffered.instances else 0
        return printed, result, seen, put_back

    def test_a_connection_that_goes_quiet_is_noticed_and_retried_on_deepgram(self):
        # Answers every 0.3 s for three seconds, then nothing: open on this side, gone on the other.
        def behaviour(index, age):
            return index > 0 or age < 3.0
        started = time.monotonic()
        printed, result, seen, put_back = self.run_loop(behaviour, seconds=14)
        text = "\n".join(printed)
        self.assertEqual(result, "shutdown")
        self.assertIn("staying on Deepgram and reconnecting", text)
        self.assertNotIn("Speechmatics takes over", text)
        self.assertGreaterEqual(seen["connections"], 2, "it reconnected")
        gap = seen["started"][1] - seen["started"][0]
        self.assertLess(gap, 8.5, f"noticed within about four seconds of the silence, reconnected after {gap:.1f}s")
        self.assertGreater(seen["bytes"][1], 0, "audio flows again on the new connection")
        self.assertGreater(put_back, 0, "what had gone unanswered was put back")
        self.assertIn("Putting back", text)

    def test_a_provider_that_never_answers_is_given_up_on_after_three(self):
        printed, result, seen, _ = self.run_loop(lambda index, age: False, seconds=60)
        text = "\n".join(printed)
        self.assertEqual(result, "fallback")
        self.assertIn("3 line problems in a row; Speechmatics takes over", text)
        self.assertEqual(seen["connections"], 3)
        gap = seen["started"][1] - seen["started"][0]
        self.assertLess(gap, 3.9, f"a connection that gives no first answer is replaced in about 2.5 s, not 4; took {gap:.1f}s")

    def test_deepgram_only_never_hands_over(self):
        # The same silent provider that makes the normal engine give up after three, run as the Windows app runs it.
        printed, result, seen, _ = self.run_loop(lambda index, age: False, seconds=24, deepgram_only=True)
        text = "\n".join(printed)
        self.assertEqual(result, "shutdown", text[-500:])
        self.assertNotIn("Speechmatics", text)
        self.assertGreaterEqual(seen["connections"], 4, "kept asking Deepgram, past the three that used to end it")

    def test_a_provider_that_has_answered_is_retried_past_three(self):
        # Answers for two seconds on every connection, then goes quiet: a flaky line, not a blocked host.
        printed, result, seen, _ = self.run_loop(lambda index, age: age < 2.0, seconds=34)
        text = "\n".join(printed)
        self.assertEqual(result, "shutdown", text[-600:])
        self.assertNotIn("Speechmatics takes over", text)
        self.assertGreaterEqual(seen["connections"], 4, "kept trying beyond the three a silent provider gets")

    def test_muted_audio_is_not_taken_for_a_dead_connection(self):
        # Nothing is answered, but nothing is being sent either (muted: only KeepAlive goes out). That is not a dead line.
        printed = []

        async def main():
            app = tempfile.mkdtemp(prefix="line-")
            Path(app, "pause.flag").write_text("1")
            seen = {"connections": 0}

            async def handler(ws):
                seen["connections"] += 1
                async for _ in ws:
                    pass
            async with websockets.serve(handler, "127.0.0.1", 0) as server:
                port = server.sockets[0].getsockname()[1]
                FakeBuffered.instances.clear()
                scope = engine_scope(app, f"ws://127.0.0.1:{port}/", FakeBuffered, printed)
                runner = asyncio.create_task(scope["_run_deepgram_loop"]({}))
                await asyncio.sleep(0.5)
                buffered = FakeBuffered.instances[0]
                buffered.app = app
                buffered.stop_at = time.monotonic() + 9
                await asyncio.wait_for(runner, 30)
                return seen["connections"]
        connections = asyncio.run(main())
        self.assertEqual(connections, 1, "nine seconds muted, still one connection: " + " | ".join(printed[-4:]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
