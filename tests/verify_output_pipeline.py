#!/usr/bin/env python3
"""
Does the answer cleaner damage code?

The version of this file that this one replaces asked a different and much
weaker question. It grepped the C# for the string "TransformProseOnly(" and
confirmed both cleaners called it. That is a plumbing check: it proved the
calls were wired up and said nothing about what came out the other end. It
passed, green, for the entire period during which the cleaner was corrupting
every unfenced line of code it was given.

So this one runs the real patterns over real code and compares bytes.

The patterns are READ OUT OF THE C# SOURCE rather than copied here. A copy
would be a second spelling of the rule, and a second spelling that drifts is
the exact failure this whole area keeps producing - the two cleaners each
carried their own nearly-identical regex, and the fix went into one of them.
If someone edits the C#, this test exercises the edit.

Run:  python tests/verify_output_pipeline.py
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "ScreenAnalyzer.cs")

# ── Pull the patterns out of the C# ──────────────────────────────────────────

FIELD = re.compile(
    r"private\s+static\s+readonly\s+Regex\s+(\w+)\s*=\s*\n?\s*new\((.*?)\);",
    re.S)
LITERAL = re.compile(r'@"((?:[^"]|"")*)"', re.S)


def load_patterns(path):
    src = io.open(path, encoding="utf-8-sig").read()
    out = {}
    for name, body in FIELD.findall(src):
        parts = LITERAL.findall(body)
        if parts:
            out[name] = "".join(p.replace('""', '"') for p in parts)
    return out


P = load_patterns(SRC)

REQUIRED = ["FencedBlock", "BareCodeSection", "RxBoldStrict", "RxItalicStrict",
            "RxUnderDouble", "RxUnderSingle", "RxAtxHeading"]
missing = [r for r in REQUIRED if r not in P]
if missing:
    print("FAIL  could not find these regexes in ScreenAnalyzer.cs: %s" % ", ".join(missing))
    sys.exit(1)

FENCE = re.compile(P["FencedBlock"], re.S)
SECTION = re.compile(P["BareCodeSection"], re.M)
EMPHASIS = [re.compile(P[n]) for n in
            ("RxBoldStrict", "RxItalicStrict", "RxUnderDouble", "RxUnderSingle")]
ATX = re.compile(P["RxAtxHeading"], re.M)


def strip_emphasis(s):
    for rx in EMPHASIS:
        s = rx.sub(r"\1", s)
    return ATX.sub("", s)


def clean(text):
    """TransformProseOnly + StripEmphasis, in the order the C# runs them."""
    stash = []

    def grab(m):
        stash.append(m.group(0))
        return "%d" % (len(stash) - 1)

    masked = SECTION.sub(grab, FENCE.sub(grab, text))
    out = strip_emphasis(masked)
    for i, v in enumerate(stash):
        out = out.replace("%d" % i, v)
    return out


# ── What must survive ────────────────────────────────────────────────────────

CODE = [
    # The line that started it. Pasted into LeetCode it gave "invalid argument
    # type 'ListNode' to unary expression", over and over, while the server log
    # showed the code leaving correct.
    "ListNode* insertionSortList(ListNode* head) {",
    "int *a, *b;",
    "*dst = *src;",
    "void f(char **argv, int *n)",
    "def f(*args, **kwargs):",          # not a pointer at all
    "area = w * h * depth;",            # nor is this
    "/* copy */ memcpy(dst, *src, n);",
    "user_name = get_user_name(user_id)",   # the underscore rule's reach
    "std::vector<int>* p = &v;",
    "a *= 2; b **= 3;",
    "x = y_1 * z_2;",
    "p = *q++;",
    "__init__ and __repr__",            # only survives inside a code region
]

MARKDOWN = [
    ("This is **important** to say", "This is important to say"),
    ("Use *emphasis* sparingly", "Use emphasis sparingly"),
    ("The _key_ idea", "The key idea"),
    ("Both **bold** and *italic* here", "Both bold and italic here"),
    ("__strong__ text", "strong text"),
    ("## Header here", "Header here"),
]

failures = []


def report(ok, label, detail=""):
    print("%s  %s%s" % ("ok  " if ok else "FAIL", label, detail))
    if not ok:
        failures.append(label)


print("1. Code inside a fence")
for c in CODE:
    body = "```python\n%s\n```" % c
    report(c in clean(body), c)

print("\n2. Code under a bare SOLUTION heading, no fence")
body = "APPROACH\nTwo pointers.\nSOLUTION\n" + "\n".join(CODE) + "\nCOMPLEXITY\nTime: O(n)\n"
got = clean(body)
for c in CODE:
    report(c in got, c)

print("\n3. Markdown in prose is still removed")
for src_line, want in MARKDOWN:
    g = clean(src_line)
    report(g == want, src_line, "" if g == want else "\n         got %r want %r" % (g, want))

print("\n4. A whole answer survives a round trip")
answer = (
    "SAY THIS\n"
    "I'd sort it with **merge sort** to keep it stable.\n"
    "APPROACH\n"
    "Insertion sort on a linked list, *in place*.\n"
    "SOLUTION\n"
    "ListNode* insertionSortList(ListNode* head) {\n"
    "    ListNode *dummy = new ListNode(0), *curr = head;\n"
    "    while (curr) { ListNode* next = curr->next; }\n"
    "    return dummy->next;\n"
    "}\n"
    "COMPLEXITY\n"
    "Time: O(n^2)   Space: O(1)\n"
)
out = clean(answer)
report("ListNode* insertionSortList(ListNode* head) {" in out, "pointer signature intact")
report("ListNode *dummy = new ListNode(0), *curr = head;" in out, "declaration intact")
report("ListNode* next = curr->next;" in out, "inner pointer intact")
report("merge sort" in out and "**" not in out.replace("**kwargs", ""), "prose bold stripped")
report("in place" in out and "*in place*" not in out, "prose italic stripped")

print("\n" + "=" * 60)
if failures:
    print("%d FAILED" % len(failures))
    for f in failures:
        print("  - %s" % f)
    sys.exit(1)
print("all passed")
