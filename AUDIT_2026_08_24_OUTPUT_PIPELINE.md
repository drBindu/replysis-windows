# Output pipeline audit — 24 August 2026

**Scope:** everything that touches an AI answer between the backend sending it and
the candidate reading it, in both answer paths (spoken question, and F8 screen
analysis).

**Why this area:** a user spent an evening pasting code that would not compile,
while the server log showed the same answers leaving correct. That gap — correct
on the wire, broken on the screen — is only possible in this layer, and nothing
in it had ever been reviewed as one piece.

**Result:** five defects, four of them the same mistake repeated, all shipping to
users, none of them visible in any server-side test.

---

## The shared mistake

Four of the five bugs are one idea applied four times: **a text rule written for
English prose, run over the whole answer including its code.**

The characters that mark emphasis in markdown — `*` and `_` — are ordinary
syntax in every C-family language. Whitespace runs that are noise in a sentence
are structure in a program. A rule that is correct for one is destructive for the
other, and applying it to a mixed document silently corrupts the half it was
never meant to touch.

Nothing failed loudly. Every one of these produced output that looked plausible
and was wrong.

---

### BUG A — Markdown italic rule deleted every C++ pointer (CRITICAL)

**File:** `ScreenAnalyzer.cs` — `PostProcess()`
**Rule:** `Regex.Replace(raw, @"\*([^*\n]+)\*", "$1")` — strip `*italic*`

The rule matches from one asterisk on a line to the next and removes both. On

```cpp
ListNode* insertionSortList(ListNode* head) {
```

it reads `* insertionSortList(ListNode*` as italicised text and produces

```cpp
ListNode insertionSortList(ListNode head) {
```

which does not compile: `invalid argument type 'ListNode' to unary expression`.
It also rewrote `ListNode *next;` to `ListNode next;` inside the definition
comment.

**Impact:** every pointer-based answer — linked lists, trees, graphs — arrived
broken. This is the single defect behind an entire evening of failed pastes.
It was invisible from the server, which is where the fix was repeatedly and
wrongly attempted: the answer was correct when it left the model, correct in the
log, and destroyed after it arrived.

**Fix:** fenced blocks are lifted out before any prose rule runs and restored
afterwards, byte for byte.

---

### BUG B — Same rule, same file, second copy, in the spoken-answer path (CRITICAL)

**File:** `MainWindow.xaml.cs` — `CleanAiOutput()`
**Rule:** `Regex.Replace(ans, @"\*{1,3}([^*\n]+)\*{1,3}", "$1")`

The identical defect on the other path, so a spoken answer containing code was
corrupted the same way. Its sibling rule, `_{1,3}([^_\n]+)_{1,3}`, had the same
reach over `snake_case` identifiers sharing a line — `my_count` and `my_total`
on one line would both lose their underscores.

**Why it survived:** the two cleanup routines were written separately and
neither knew about the other, so a fix to one could never reach the second.

**Fix:** both now call one shared, documented helper,
`ScreenAnalyzer.TransformProseOnly(text, transform)`. The protection cannot be
present in one path and missing from the other because there is only one
implementation.

---

### BUG C — Whitespace collapsing flattened all code indentation (HIGH)

**File:** `MainWindow.xaml.cs` — `CleanAiOutput()`
**Rule:** `Regex.Replace(ans, @"[ \t]{2,}", " ")` — collapse doubled spaces

Correct for prose. Applied to a whole answer it rewrites every indented line of
code to a single leading space, so a class arrives flattened against the left
margin — unreadable in the panel it is about to be pasted from, and visibly not
what a competent candidate would have written.

**Fix:** prose only.

---

### BUG D — Word swaps and bullet rewriting reached into code (MEDIUM)

**File:** `MainWindow.xaml.cs` — `CleanAiOutput()`

Two more prose rules with the same reach:

* the AI-tell word swaps (`leverage` → `use`, `robust` → `solid`, …) would rename
  a variable legitimately called `robust` inside working code;
* the bullet normaliser, `^[ \t]*[-*–—]\s+` → `• `, would rewrite a code line
  beginning with `-` or `*` — a pointer declaration, a decrement, a comment — into
  a bullet.

Lower frequency than A–C, identical in kind. **Fix:** prose only.

---

### BUG E — Screen answers never reached the code panel (HIGH, UX)

**File:** `MainWindow.xaml.cs` — F8 screen-analysis handler

The spoken path called `ShowAnswer()`, which lifts code into the SOLUTION panel
and shows a **Copy code** button. The F8 path assigned straight to
`AiAnswerBox.Text` and never called it — so for screen analysis, the panel and
its copy button never appeared at all.

That is the one place they matter most: a screen answer is usually the code the
candidate is about to paste. Instead they were copying it out of a paragraph by
hand, fence markers included, guessing where the code started and stopped. The
panel had existed the whole time; this path simply never reached it.

**Fix:** the F8 path now calls `ShowAnswer()` and reassembles the header and
prior answers around the cleaned prose it returns.

---

### Also cleaned

Raw control characters (`\x00`, `\x01`) had been written into `ScreenAnalyzer.cs`
as placeholder sentinels. They worked, but a NUL byte in source is fragile
across editors, diffs and encodings. Replaced with escaped Private Use Area
code points (``, ``), which cannot occur in model output and survive
any text tool.

---

## Verification

Ten structural assertions run against both files
(`verify_paths.py` in the scratch directory), all passing:

* neither file contains raw control characters
* no emphasis rule anywhere runs unprotected on a whole answer
* both cleaners route through the single shared helper
* the F8 path calls `ShowAnswer`

Plus a behavioural test on a realistic answer: pointers **and** `snake_case`
preserved through both cleaners, while `**bold**` and `*italic*` are still
stripped from prose.

---

## The lesson worth keeping

Every one of these was a **client** bug, and every attempted fix for the
symptom was made on the **server**, because that is where the answer is
generated and where the failure appeared to originate. The server kept
reporting success because the server was never wrong.

The diagnosis took one comparison: log what the server *sent* beside what the
screen *showed*. That check is now permanent — the backend logs
`[SCREEN_SIG] before=[…] after=[…]` for every coding answer — so the next time
these two disagree, it is one line of evidence rather than an evening of
inference.

**Rule going forward:** any transform applied to an AI answer must state whether
it is prose-safe or code-safe. If it is prose-only, it goes through
`TransformProseOnly`. There is no third option, and no reason to write a new
stashing implementation.
