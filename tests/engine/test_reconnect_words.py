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


def load_overlap():
    text = open(SOURCE, encoding="utf-8-sig").read()
    start = text.index("def _drop_replayed_words(")
    end = text.index("# A Flux connection that sat muted goes stale.")
    namespace = {}
    exec(compile(text[start:end], "engine_reconnect_words", "exec"), namespace)
    return namespace["_replay_overlap"], namespace["_without_last_word"]


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

    def test_a_cut_last_word_is_replaced_by_the_whole_one(self):
        overlap, without_last = load_overlap()
        held = "how would you build a url short"
        text, cut = overlap(held, "a url shortener that scales")
        self.assertTrue(cut)
        self.assertEqual(f"{without_last(held)} {text}", "how would you build a url shortener that scales")
        # Later messages of the same turn repeat the overlap; with the cut word gone they match on one word or more.
        text, cut = overlap(without_last(held), "url shortener that scales to millions", 1)
        self.assertFalse(cut)
        self.assertEqual(text, "shortener that scales to millions")

    def test_a_word_that_only_starts_alike_after_no_overlap_is_not_cut(self):
        overlap, _ = load_overlap()
        self.assertEqual(overlap("tell me about the", "then what happened"), ("then what happened", False))
        self.assertEqual(overlap("we used the", "where the data lives"), ("where the data lives", False))

    def test_the_engine_keeps_words_across_connections(self):
        text = open(SOURCE, encoding="utf-8-sig").read()
        body = text[text.index("async def _run_deepgram_loop"):]
        self.assertNotIn('transcript = {"confirmed": "", "partial": ""}', body[:body.index("async def sender")])
        self.assertIn("close_timeout=1", body)


if __name__ == "__main__":
    unittest.main()
