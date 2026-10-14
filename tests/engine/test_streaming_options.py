"""Exercise the shipping provider request without a key or network connection."""
import ast
from pathlib import Path
from types import SimpleNamespace
import unittest
import urllib.parse

source = Path(__file__).resolve().parents[2] / "speechmatics_engine.py"
tree = ast.parse(source.read_text(encoding="utf-8"))
names = {"_deepgram_keyterms", "_deepgram_url", "_deepgram_flux"}
scope = {"os": __import__("os"), "urllib": urllib, "args": SimpleNamespace(utterance_silence=1.0), "SAMPLE_RATE": 16000,
         "DEEPGRAM_URL": "wss://api.deepgram.com/v1/listen", "DEEPGRAM_MAX_KEYTERMS": 100,
         "_DEEPGRAM_BUILTIN_TERMS": ["Replysis", "Python", "Java", "Kubernetes"],
         "_load_extra_vocab": lambda: []}
constants = {"DEEPGRAM_URL", "DEEPGRAM_MAX_KEYTERMS", "_DEEPGRAM_BUILTIN_TERMS", "SAMPLE_RATE"}
for node in tree.body:
    if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in constants for t in node.targets):
        exec(compile(ast.Module(body=[node], type_ignores=[]), str(source), "exec"), scope)
exec(compile(ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names],
                        type_ignores=[]), str(source), "exec"), scope)


class StreamingOptions(unittest.TestCase):
    def test_entity_formatting_cannot_hold_up_live_words(self):
        query = urllib.parse.parse_qs(urllib.parse.urlsplit(scope["_deepgram_url"]()).query)
        self.assertEqual(query["no_delay"], ["true"])
        self.assertEqual(query["interim_results"], ["true"])
        self.assertEqual(query["endpointing"], ["200"])
        self.assertEqual(query["sample_rate"], ["16000"])

    def test_candidate_terms_precede_generic_dictionary_and_deduplicate(self):
        scope["_load_extra_vocab"] = lambda: [{"content": t} for t in ["AcmePlatform", "Python", "python"]]
        terms = scope["_deepgram_keyterms"]()
        self.assertEqual(terms[:2], ["AcmePlatform", "Python"])
        self.assertEqual(sum(t.lower() == "python" for t in terms), 1)

    def test_long_unicode_vocabulary_is_bounded(self):
        scope["_load_extra_vocab"] = lambda: [{"content": "Company" + str(i) + "界" * 30} for i in range(170)]
        terms = scope["_deepgram_keyterms"]()
        self.assertLessEqual(sum(len(t.encode("utf-8")) + 1 for t in terms), 480)
        self.assertLessEqual(len(terms), 100)
        self.assertIn("Replysis", terms)


if __name__ == "__main__":
    unittest.main()
