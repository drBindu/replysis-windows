"""
Two things on the Speechmatics path (languages other than English) that left listening dead.

1. The session error must be forgotten before every connection, whatever kind of key the engine was given. It sat inside
   the branch that mints a raw key, so with the server's ready-made token it never ran: one error from the server and
   every reconnect after it failed at once.
2. The server's placeholder key ("speech-backup-unavailable", sent when Speechmatics had no token) must not lead to the
   FATAL line, which made the app give up for the rest of the session. It exits with code 2 and no FATAL, which is the
   app's "fetch a fresh key and start again".

Checked against the real engine source. Run: python tests/engine/test_speech_key_and_session_error.py
"""
import os
import unittest

SOURCE = os.path.join(os.path.dirname(__file__), "..", "..", "speechmatics_engine.py")


def source():
    return open(SOURCE, encoding="utf-8-sig").read()


def load_key_helper():
    text = source()
    start = text.index("SPEECH_KEY_UNAVAILABLE = ")
    end = text.index("def mint_realtime_jwt(")
    namespace = {}
    exec(compile(text[start:end], "engine_speech_key", "exec"), namespace)
    return namespace["_speech_key_unavailable"]


def connection_block():
    text = source()
    start = text.index("        for endpoint in endpoints:")
    end = text.index("                ws = WebsocketClient(settings)", start)
    return text[start:end]


def indent_of(block, needle):
    for line in block.splitlines():
        if needle in line:
            return len(line) - len(line.lstrip(" "))
    raise AssertionError(f"not found: {needle}")


class SpeechKeyAndSessionError(unittest.TestCase):
    def test_placeholder_and_empty_keys_are_unavailable(self):
        unavailable = load_key_helper()
        self.assertTrue(unavailable("speech-backup-unavailable"))
        self.assertTrue(unavailable("  speech-backup-unavailable  "))
        self.assertTrue(unavailable(""))
        self.assertTrue(unavailable(None))
        self.assertFalse(unavailable("eyJhbGciOiJIUzI1NiJ9.eyJ4IjoxfQ.sig"))
        self.assertFalse(unavailable("a" * 32))

    def test_session_error_is_cleared_for_every_kind_of_key(self):
        block = connection_block()
        # Same level as the code that runs for every key, not inside the raw key branch.
        self.assertEqual(indent_of(block, "_clear_ws_session_error()"), indent_of(block, "url = endpoint"))
        self.assertEqual(indent_of(block, "Connecting to {endpoint}"), indent_of(block, "url = endpoint"))

    def test_placeholder_exits_two_before_any_fatal(self):
        block = connection_block()
        check = block.index("if _speech_key_unavailable(raw_key):")
        exit_two = block.index("sys.exit(2)", check)
        mint = block.index("mint_realtime_jwt(raw_key)")
        self.assertLess(check, mint)
        self.assertLess(exit_two, mint)
        # Nothing printed between the placeholder check and its exit says FATAL (comments may mention it).
        printed = [line for line in block[check:exit_two].splitlines() if "print(" in line]
        self.assertTrue(printed)
        self.assertFalse(any("FATAL" in line for line in printed))


if __name__ == "__main__":
    unittest.main()
