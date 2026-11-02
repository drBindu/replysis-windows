"""
A drop in the middle of a question must not lose the words before it, and the audio sent again must not double them.

The helper is taken out of the real engine source and run here, so this checks what ships.
Run: python tests/engine/test_reconnect_words.py
"""
import os
import unittest

SOURCE = os.path.join(os.path.dirname(__file__), "..", "..", "speechmatics_engine.py")


def load_helper():
    text = open(SOURCE, encoding="utf-8-sig").read()
    start = text.index("def _drop_replayed_words(")
    end = text.index("# A Flux connection that sat muted goes stale.")
    namespace = {}
    exec(compile(text[start:end], "engine_reconnect_words", "exec"), namespace)
    return namespace["_drop_replayed_words"]


class ReconnectWords(unittest.TestCase):
    def setUp(self):
        self.drop = load_helper()

    def test_replayed_words_are_removed(self):
        self.assertEqual(self.drop("tell me about your last project", "your last project and what you did"), "and what you did")

    def test_case_and_punctuation_do_not_matter(self):
        self.assertEqual(self.drop("what was your last project?", "Last project. Why?"), "Why?")

    def test_one_matching_word_is_kept(self):
        self.assertEqual(self.drop("tell me about the", "the role you had"), "the role you had")

    def test_nothing_new_yet(self):
        self.assertEqual(self.drop("your last project", "last project"), "")

    def test_no_overlap_and_nothing_held(self):
        self.assertEqual(self.drop("one two three", "four five"), "four five")
        self.assertEqual(self.drop("", "four five"), "four five")

    def test_the_engine_keeps_words_across_connections(self):
        text = open(SOURCE, encoding="utf-8-sig").read()
        body = text[text.index("async def _run_deepgram_loop"):]
        self.assertNotIn('transcript = {"confirmed": "", "partial": ""}', body[:body.index("async def sender")])
        self.assertIn("close_timeout=1", body)


if __name__ == "__main__":
    unittest.main()
