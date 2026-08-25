# What the Windows app learned, for the Mac app

Hand this to the Mac session. It is written to be read cold, by someone with
no memory of the Windows work.

Everything here came from one person testing in front of a real screen. None
of it was found by reading code, which is why the reasoning matters more than
the diffs: the same mistakes are waiting in any client that talks to the same
backend.

---

## The backend is shared, so half of this is already yours

The Mac app talks to the same server. These are live and need nothing from
the Mac side:

**Answers no longer depend on one model.** Order is `gpt-oss-20b`, then
`gpt-oss-120b`, then OpenAI if the account is active. Groq meters tokens per
model, so the second is a whole separate allowance, not a share of one. The
fallback used to be OpenAI alone, and when that account lapsed a rate limit
became a hard failure.

**Screen analysis runs on Groq**, `qwen/qwen3.6-27b`, free on the same key.
The code used to say no Groq vision model existed. It was wrong: asking each
model for an image is how you find out, and qwen answers "image must have at
least 2 pixels", which is a complaint about the test pixel and means it read
the image.

**Coding answers are written by a different model than the one that reads the
screen.** The vision model reports what is there — statement, language,
existing code, the error and its line — and `gpt-oss-120b` writes the answer
from that description, never seeing the picture. Asked to fix an LRU Cache,
the vision model had produced three implementations across three attempts,
each broken differently: `Node head, tail;` declared as objects then used
with `head->nxt`, a variable used after being deleted, a single method with
no class around it. That is not a prompting problem, it is a 27B vision model
being asked to write correct C++.

**Screenshots can be sent ahead.** `POST /api/v1/interview/screen-cache` with
`{image}` returns `{imageId}`; the question then carries `imageIds` instead
of the bytes. Held in memory ninety seconds, returned only to the identity
that sent it, and returned exactly once. Sending bytes inline still works.

**Several views of one screen are accepted.** `imageIds` takes up to three,
oldest first; the model is told they are one page read top to bottom.

**Listening time is metered and capped.** `POST /api/v1/usage/listening` with
`{minutes}`. Free 60 minutes a month, pro 900, max 1800, written to
`audioMinutesUsed` on the user document — the same field the website uses, so
one allowance covers every client. `GET /api/v1/stt/key` returns 402 with
`{"reason":"audio-limit"}` once it is gone. **The Mac app is capped by this
but does not report to it**, so its minutes never accumulate. That is the
largest single gap.

---

## What the Mac app has to build itself

### 1. Do not throw away the speech token on quit

**Renewal margin is five minutes, not one.** Windows shipped sixty seconds and
that was wrong: a token accepted with sixty-one seconds left opens a session
that dies mid-answer, in front of an interviewer. Tokens last an hour, so
renewing at fifty-five minutes rather than fifty-nine is still about one an
hour against an allowance of twelve. The Mac session reached five minutes
independently and argued it correctly; sixty seconds had nothing behind it but
being a round number. Windows now matches.


The token is good for about an hour and was kept in memory only, so every
launch spent a new one against a twelve-per-hour allowance. A dozen restarts
locked the account out — on both apps at once, because the allowance is per
account.

Persist it, encrypted, and reuse it until it actually expires. Delete the
cached copy whenever a token is rejected, or a dead one survives on disk for
its full hour and nothing on screen explains the failure.

### 2. "Contract blocked" is not a transient error

Speechmatics answers `{'type': 'not_allowed', 'reason': 'Contract blocked:
Credit Balance Exhausted'}` when the balance runs out. It matched nothing, so
the engine retried on a doubling backoff forever and the UI said
"connecting". Treat it as an auth failure: drop the cached token, say plainly
that the account has no credit, and it will recover by itself when billing is
restored.

### 3. Read a sentence before answering it

Silence cannot tell "finished" from "still thinking". Both directions were
reported days apart: answering mid-question, then feeling sluggish.

Classify how the transcript ends and wait accordingly:

- **Finished** — punctuation, landing on a real word. 300ms.
- **Unclear** — punctuation, but landing on a preposition, conjunction or
  determiner. A question mark after "for" is the engine hearing a breath, not
  the speaker stopping: "What are you looking for?" was followed by "C2C or
  W2 or full time". 820ms, or 1.3x that speaker's own longest pause, capped.
- **Unfinished** — hanging with no punctuation at all. Never submit; their
  next word submits it.

Pronouns must not be in the strict list. "How would you scale this?" is a
finished question.

**This applies to push-to-talk too.** People press the key the moment they
stop talking and often a beat before, so the flush waits 800ms instead of
100ms when the transcript plainly has not finished. Telling users to press
more carefully is not a fix.

### 4. Join a continuation instead of answering half

When a tail arrives within twelve seconds — short, adds something, and either
opens with a joining word or asks nothing by itself — merge it with the
question already asked and answer the whole thing, replacing what is on
screen.

"Asks nothing" is the test, not "is not a sentence". "C2C or W2 or full
time." is well formed and is obviously the rest of "what are you looking
for". Exclude fillers by name, or "okay" after an answer re-runs the previous
question and spends a credit.

### 5. Ask what the candidate wants, because no resume says

"Are you looking for C2C or W2 or full time?" is asked in the first two
minutes of nearly every US contract screen, and the answer appears on no
resume ever written. With nothing to go on the app produced a paragraph about
wanting to grow and learn, which answers none of it and reads to a screener
as dodging a direct question.

Interview Setup now carries five things, saved with the company and role so
they are answered once rather than before every interview:

- **Work type** — C2C (corp to corp), W2 contract, C2H, full time, 1099,
  open to any
- **Work authorization** — citizen, green card, H1B, H4 EAD, OPT, CPT, TN,
  L2 EAD, no sponsorship needed, will need sponsorship, prefer not to answer
- **Can start** — immediately through two months, or flexible
- **Where** — remote, hybrid, onsite, open to relocation
- **Pay** — free text, because "$65/hr on C2C" and "$140k base" are not the
  same shape

Anything left on "Not specified" is left out of the prompt entirely. A blank
must never become a confident answer: inventing a visa status or a rate on
somebody's behalf is worse than saying it is open, and both are things the
recruiter writes down.

When these are set the answer leads with the answer — "I'm looking for C2C,
and I can start in two weeks" — with one line of flexibility after it if
true, rather than a paragraph about growth.

### 6. The resume, and what happens without one

**Reload the last resume on launch.** Resumes were being saved and listed but
never put back, so every launch started with an empty box and nothing said
so: the panel is collapsed by default and an empty card looks like a normal
one. The user had uploaded the file days earlier and reasonably believed the
app still had it.

**With no resume, the model must not invent a field.** The rules forbade
inventing employers, dates and metrics, and said nothing about a tech stack.
So a Gen AI and Python candidate was told to say they wanted to keep building
on their backend experience, "especially with Java and Spring Boot". Fluent,
confident, and about a different person — with nothing to go on the model
reaches for the most common CV in existence, which is the one failure that
sounds most convincing.

It must stay stack-neutral or follow the words the interviewer used.
Technical depth is untouched: the limit is on claiming a background, never on
answering.

### 7. Teach the speech engine the words this interview uses

Letters and numbers are what speech recognition gets worst, and they open
almost every contract screen. "C2C" came through as "See to see", "W2" as "w
to", and the candidate got an answer to a question nobody asked.

Added to the vocabulary with every spoken form recruiters actually use, since
the same term arrives as "C two C", "C to C" and "corp to corp" from three
different people in a week: C2C, W2, 1099, corp to corp, contract to hire,
H1B, OPT, CPT, EAD, green card, visa, notice period, relocation, onsite,
hybrid, remote.

**And tell the model the question came through speech recognition.** It
should read for what was meant, not the letters that arrived. A short mapping
in the prompt costs little and catches what the vocabulary misses.

Note: the vocabulary list is rejected outright by some Speechmatics models —
melia-1 validates against a different schema and refuses `additional_vocab`,
`punctuation_overrides`, `enable_entities`, `max_delay` and
`max_delay_mode`. Asking for it took transcription down completely, on every
endpoint, on every retry.

### 8. Screenshots: what took a day to learn

**Take it before the question.** In a watch mode every question is a screen
question, so capture on a timer and keep one ready. Removes capture and
encode from the wait entirely.

**Send it before the question too.** That was the larger half: 1,483ms to
first word, of which the model was 720ms and most of the rest was the picture
going up the wire.

**Do not re-send a still screen.** A page with a live "2,332 Online" counter
produces different bytes every two seconds. Compare a coarse signature —
16x16, sixteen greys — so scrolling counts and a ticking counter does not.
Comparing exact bytes doubled the token cost of every question.

**A whole monitor needs more resolution than a window.** At a 768 short edge a
1920x1080 screen becomes 1365x768 and body text goes from fourteen pixels to
ten, which is where a vision model stops reading and starts recalling. The
evidence was an answer that named Two Sum, described the right approach, and
never mentioned "Compile Error" printed in red across half the same screen.

**Watching means the whole screen.** Targeting the foreground window is right
for an explicit hotkey and wrong for a mode left running, where the target
becomes whichever window was clicked last.

### 9. Say what cannot be seen, then answer again when it can

A coding problem rarely fits on one screen. If the statement is cut off, give
the candidate a line to say out loud — "let me scroll down and read the
constraints before I answer" — and name what is missing.

Then **answer again by yourself when the screen changes.** The first version
asked them to scroll and then ignored them for doing it; they had to work out
that they should ask the same question twice. Once only, within twenty-five
seconds, and never while an answer is streaming or they are speaking.

### 10. Answer the question, not the screen

Three separate failures, all the same shape:

- Asked "you can see my screen, right? Can you solve this?", it confirmed it
  could see the screen and never solved anything. There is one real question
  there and it is the second.
- Asked "which language do you prefer?" while watching, it answered about a
  code editor. Watching a screen does not make every question about it, and
  behavioural questions are most of an interview.
- Given a half-transcribed question, it listed the problem number and the
  selected language while asking for the rest. Ask in one line and stop.

And the one nobody asks for: **if the screen shows a compile error or a
failed test, lead with it.** Nobody in an interview says "can you solve that
error" — they wait to see whether you notice.

### 11. The screen workflow, and where the controls live

This is the shape the whole feature settled into. Build this, not the earlier
version described in the changelog below.

**Answering from the screen is a setting, on by default.** Settings →
"Answer from the shared screen", remembered between launches.

It used to be a toolbar switch that started off every time, which meant the
feature most likely to matter in a coding round was the one a candidate had
to remember to arm — with an interviewer already talking. Nobody reads a
toolbar under that pressure, and a feature that must be armed is one most
people never see work.

**The toolbar holds the action, not the arming.** A button reading READ
SCREEN with F8 beside it. Pressing it reads the screen there and then, which
is the same thing F8 has always done silently. Its label does not change when
pressed: a control whose text changes is read as a switch, and this one is
not. Only its colour says whether screen answers are armed.

**The three hotkeys, precisely.** All global — they work while another
application has focus, which is the entire point, and they are ignored while
our own window is in front so they never fire on the app itself.

| Key | Captures | Use |
|---|---|---|
| **F8** | the window currently in front | the usual one |
| **F9** | the primary monitor, whole | when the thing is not in a window |
| **F7** | a box the user drags | one part of a crowded screen |

**What pressing F8 actually does, in order:**

1. Hides the app's own windows from the capture, so the answer is not
   photographed into the next question.
2. Captures the foreground window — which is whatever application the user is
   in, because our window is not focused when a global hotkey fires.
3. Downscales, encodes, sends, and streams the answer back.
4. Restores the windows.

**Nothing is spoken and nothing is typed.** No microphone, no Space, no
question. The screen is the question. That is what makes it usable while
somebody is talking to you: to the room, the candidate looked at their screen
and nothing else happened.

**F8 works whether or not the setting is on.** They are independent: the
setting decides whether a *spoken* question may be answered from the screen;
F8 is the candidate deciding to look, on demand, always available.

**F8 is sharper than the setting's capture.** A single window arrives close
to its real size; the whole monitor is shrunk to fit and small text suffers.
So for reading a compile error or a dense problem statement, F8 is the better
of the two — and it is also faster, because one window is fewer pixels than
one screen.

**What each path is for:**

- **F8 / the button** — the candidate deciding to look. Silent, nothing
  spoken, reads the window they are in, sharper because a single window
  arrives near its real size. This is the one to use after running code.
- **The setting** — the interviewer asking about something on screen, with
  no keypress. Reads the whole monitor, so it does not depend on which window
  was clicked last.

**The workflow it produces:**

    Coding round starts    -> nothing to switch on
    Problem appears        -> scroll through it once while reading
    They ask about it      -> answered from the screen
    They ask about you     -> answered normally, no screenshot
    Code was run           -> F8, and it names the error

The point is that nothing has to be remembered. Every earlier version of this
required the candidate to do something at the exact moment they were least
able to.

### 12. Code belongs in its own panel

Prose and code were sharing one wrapped, proportional-font box. Indentation
collapsed, long lines folded mid-expression, and the part that has to be read
most carefully was the hardest thing on screen to read.

Monospace, no wrapping, its own scrollbars, a copy button, complexity
underneath. The prompts must emit fenced code for this to work, and anything
stripping fences before display has to stop.

---

## Things that will look like bugs and are not

**"The AI service is temporarily unavailable"** during testing is almost
always the free Groq tier: 8,000 tokens a minute, and one full-screen view
costs 1,809 of them. Four screen questions a minute. It is now reported as a
rate limit with a wait, not as an outage.

**`detail: low` does not help.** Measured: 1,809 prompt tokens either way on
this model. Image size is the only lever, and image size is what makes text
readable.

---

## Changes since this file was first written

Kept up to date as the Windows app changes, so nothing has to be rediscovered
by reading diffs. Newest first.

**The worked-example fix below also failed in front of the real user, one
request after four clean isolated trials said it was solid. Third attempt at
the same bug, and it moved from "ask the model to fix it" to "fix it in code
before the model sees it" — a regex, not a prompt, and it is unit-tested
separately from the app. Also added: the Analyze hotkey never had permission
to say a problem statement was cut off, on either side, so it never asked to
scroll.**

Three attempts at one bug, in order, each looking solved until the next real
request: an abstract rule ("match pointers and objects") — satisfied in
prose, skipped in code. A worked example ("this happened, was sent to a real
user, and did not compile") — passed four isolated trials in a row, then
failed on the very next real one, same shape, the model again stating the
fix out loud and not applying it. Both were still asking an LLM to reliably
do the same small thing every time, and an LLM does not do anything reliably
every time.

A fourth attempt, `fixPointerSignatureIfNeeded()`, repairs a mangled
signature server-side before stage two reads it. **This paragraph used to
say that was the fix that held. That was wrong, and the Mac session was
misled by it** — it read this and concluded the Windows fix was
signature-shaped and narrow. It could not have been the fix: the damage
happened on the client, after the server had already sent correct code.
The server repair is harmless and still runs, but it was never what
stopped the corruption.

**The fix that actually held is on the client**, in
`ScreenAnalyzer.TransformProseOnly` — and the first version of it was
still only half a fix. It lifted fenced blocks out before the markdown
rules ran and put them back byte for byte, which works only while the
model fences. Nothing asked it to. The prompt said "complete code, in
whatever language is on screen" and never mentioned a fence, so whether
code survived was decided by the model's habit. Measured against the real
regexes, unfenced: **eight of eight lines corrupted**, and only one of
them was about pointers —

    def f(*args, **kwargs):        ->  def f(args, *kwargs):
    area = w * h * depth;          ->  area = w  h  depth;
    user_name = get_user_name(x)   ->  username = getusername(x)

It was never a C or C++ bug. `\*([^*\n]+)\*` matches any paired asterisk,
and code is full of them.

The fix now has two layers, because either alone leaves a hole:

1. **Regions come out before any rewrite** — fenced blocks *and* the
   unfenced sections the prompt itself defines as code (`SOLUTION`, `FIX`,
   `CODE`, up to the next heading). See `BareCodeSection`.
2. **Emphasis requires real markdown context** — delimiters must hug
   non-space on the inside, and the terminal character classes exclude the
   delimiter itself, not just whitespace. `\S` matches `*`, which is why an
   earlier attempt still ate `**kwargs`. Underscores additionally need a
   word boundary or snake_case loses its underscores.

Both cleaners now call one `StripEmphasis()`. They previously each carried
their own copy and the copies were not even the same rule — one stripped
one-to-three asterisks, the other two — which is how a fix lands in one
path and misses the other.

Accepted limit: in prose with no surrounding code section, `__init__` is
indistinguishable from `__strong__` and is stripped. Inside a fence or a
`SOLUTION` section it survives, which is where a dunder actually appears.

The prompt now also asks for fences. That is belt and braces, not the fix —
three earlier attempts at this were all requests to the model, and the
lesson each time was that a rule the model can quietly ignore is not a fix.

`tests/verify_output_pipeline.py` used to be a string-presence check: it
grepped for `TransformProseOnly(` and confirmed both cleaners called it. It
was green throughout the period the cleaner was corrupting every unfenced
line it saw. It now reads the patterns out of `ScreenAnalyzer.cs` and runs
real code through them, fenced and unfenced, comparing bytes.

**Dunders are held by an exact list**, `PythonDunder`, masked before the
underscore rules run. The Mac session proposed this and it is right: no
shape test can separate `__init__` from `__strong__`, because they are the
same shape. A list can, and it cannot misfire, since `__strong__` is not on
it. Covers `__init__ __repr__ __str__ __len__ __main__ __name__ __doc__
__dict__ __file__ __all__ __enter__ __exit__ __new__ __call__ __iter__
__next__ __eq__ __hash__ __getitem__ __setitem__ __contains__ __slots__`.
Someone's own `__custom__` in prose outside a code section is still stripped;
that is accepted.

## Screen routing: two corrections, both from the Mac review

**The trigger now checks the setting.** The rule was

    RefersToScreen(question) || (_watchScreenMode && ...)

so the first clause ignored `_watchScreenMode` entirely. A user who had
turned screen answers **off** and then said "can you solve this?" still had
their screen captured and uploaded — and on macOS that also raises a Screen
Recording prompt for a feature they had just disabled. That was not a
decision, it was the order the clauses were written in. Both clauses check
the setting now. Pressing F8 still reads the screen whatever the setting
says, because that is someone deliberately asking rather than a phrase
caught in passing.

**The sticky bool is now a turn budget.** `_lastAnswerUsedScreen` had no
bound: once one answer came from the screen, every later question that was
not on the personal list came from the screen too, however far the
conversation had moved. It is now `_screenFollowUpsLeft`, budget 3, refilled
by an explicit screen question, drawn down by each follow-up, zeroed by
anything else.

A count rather than a stopwatch, and the Mac session's reasoning for that is
the one to keep: what ends the topic is drift, not elapsed time. "What is the
complexity?" three minutes later is still about the screen; "so tell me about
yourself" ten seconds later is not, and the personal list already catches
that. A time bound would cut the legitimate slow case while still allowing
the illegitimate fast one.

## The engine says where it came from

The Mac session put this first on the ready list and was right to: it is the
origin of everything the last two days went into. Mac shipped a 1,074-line
fork of the engine for months, every build succeeding, and nothing anywhere
would have caught it — not the build, not a test, not a support log. Nothing
would have caught it recurring either.

**Stamped through a PyInstaller runtime hook, not by editing the engine.**
The first version of this here did edit `speechmatics_engine.py` to add the
banner. That file is shared with the Mac app byte for byte, and the hash is
only worth something if both platforms compute it over the same bytes — so
that edit would have made the two hashes differ for identical logic, the
provenance check reporting a divergence it had caused itself. Worse than not
having it. The Mac session had already used a runtime hook for exactly this
reason and pointed out the trap; this side now matches. A runtime hook runs
before the main script, so the line still prints before anything can fail.

**If you change how the stamp is applied, do not change the shared source to
do it.** The two hashes being comparable is the whole mechanism.

`tools/build-engine.ps1` writes the hook at build time and the engine prints:

    >>> ENGINE BUILD: b75b845+dirty src:0fb49e3a9eb6 built:2026-08-25T01:38:18Z

Three parts, and the middle one is the point. The commit says which revision
was checked out. The source hash says whether what was compiled is actually
that revision — a commit id alone cannot tell you that, which is precisely
the gap the fork lived in. `+dirty` appears when the working tree has
uncommitted changes to the engine source.

Running from source prints `source (unstamped)`, which is a true and useful
answer rather than a failure.

`MainWindow` captures the line into `_engineBuildId` and logs it, so a support
log answers "which engine was this user running?" instead of inviting a guess.
Verified end to end in the frozen binary, not just from source.

## Comparing engine hashes: only at a shared commit

From the Mac session, and it is a rule rather than a note. A cross-platform
source-hash comparison means something **only when both sides are on the same
pushed commit**. Against a dirty working tree it can never match, and a
mismatch is then ambiguous between "we have drifted" — the thing the check
exists to detect — and "you have not pushed yet", which is not a problem at
all. An ambiguous alarm is worse than no alarm, because it trains you to
explain it away.

So: compare at a shared commit, never against a working tree. The first
attempt at comparing produced `6f9746fe6237` (Mac, `d36a7cc`) against
`f283565ab5bd` (Windows, `6684b29+dirty`) — not drift, just a Windows tree
that is ahead and unpushed.

## The deafness detector is testable, and has been watched

The decision moved out of `MainWindow` into `SpeechHealth.ShouldWarn` — pure,
static, no instance state. It was a private method reading five fields, which
meant the only way to see it work was to run a real interview and hope the
engine broke, so nobody ever had. A safety net nobody has watched catch
anything is a belief, not a net.

Eleven cases in `tests/CleanerTests/SpeechHealthTests.cs`, calling the
shipping decision rather than a copy. Three firing, eight staying quiet. The
one that matters most is "a silent room" — a false positive there would tell
someone their transcription is broken when they simply were not talking.

**That proves the rule, not the wiring.** `tools/deaf-engine-stub.py` is for
the other half: an engine that reports online, prints `MIC SIGNAL DETECTED`
forever and never once prints `PARTIAL received` or `FINAL received`. Point
the app at it and the warning must appear within twelve seconds. If it does
not, the detector is watching for lines the engine does not print, and no unit
test will ever tell you that.

Still outstanding on this side: nobody has yet run the stub against the real
app. Mac has — it fired, at exactly 15.000s.

**The threshold is the contract; the poll interval is not.** Both platforms
use a 12s threshold and consult it from a 5s meter tick, and both start the
session clock at listening start, so the ticks land at 5, 10, 15. Twelve is
never one of them. Both therefore warn at **15s**, and 12 is a number nobody
observes directly.

Stated as a rule because two platforms reporting different latencies for
identical logic would otherwise look like a bug in one of them. The honest
statement is *"warns between the threshold and the threshold plus one poll
interval"*. Asserted in `SpeechHealthTests` against `SpeechHealth.PollInterval`,
so changing either number breaks a test rather than quietly moving a figure the
two platforms compare against each other.

**Coverage differs, and Windows is broader.** On Mac the detector cannot fire
in Manual mode at all: the check lives in the listening meter and Manual never
arms it. On Windows `HandleSpaceDown` calls `StartListeningMeter()` for every
source including Manual, so the detector does run there — bounded by turn
length, since a manual turn shorter than 15s ends before the second tick.

So "we have a detector" and "the detector covers the mode the user is in" are
different claims, and the answer is not the same on both platforms. Windows:
all modes, subject to turn length. Mac: automatic modes only.

## The live transcript is swept at startup too

`latest.txt` was deleted on the way out, but only on a *clean* way out. Task
Manager, or a crash, skips the closing handler and strands the last thing an
interviewer said in plain text — the one file the app keeps that is not
encrypted for this user. The Mac session verified the same hole with
`kill -9` against `applicationWillTerminate`. Both platforms now delete on
exit **and** sweep at startup. It costs nothing: the engine rewrites the file
from scratch as soon as it starts listening.

Unit-tested standalone before touching the running app: the real bug, code
already correct (must stay untouched, not double-starred), a value parameter
never used with `->` (must stay untouched — the false-positive guard), a
second self-referential type (`TreeNode`), two defects in one text, and the
full structured shape stage one actually produces. All seven passed before
this went near production. Then five full end-to-end trials through the real
vision pipeline, on an isolated test container, before the production
container was touched at all — the same discipline the worked-example fix
used and still was not enough, so this time correctness was proven at the
mechanical layer first and only then re-proven live.

If the Mac client ever hits the same shape of bug — a model that states a
correct diagnosis and does not reliably apply it, no matter how the
instruction is worded — this is the pattern worth reaching for before a
fourth prompt attempt: find what about the broken input is mechanically,
unambiguously wrong (not "usually wrong," not "wrong unless it's a judgement
call" — actually always wrong, the way a value type used with `->` always is)
and fix that input directly, rather than trusting instructions to fix the
output every time.

Deployed the "signature is part of the code" rule below, told the user it
was fixed, and it was not: a real live request answered SAY THIS — "I'll
change the signature to take ListNode* head" — and then DETAIL opened with
the identical `ListNode insertionSortList(ListNode head)`, untouched, same
as every attempt before it. The abstract instruction described the shape of
the fix without forcing the action; the model could satisfy it in prose and
skip it in code.

Replaced the paragraph with a concrete worked example — the literal wrong
line and the literal right line, side by side, framed as "this happened,
was sent to a real user, and did not compile." Ran it four times against an
isolated test container before touching the production one this time,
specifically because the first fix looked solved after one good answer and
was not: all four produced `ListNode* insertionSortList(ListNode* head)`,
correctly, in both the parameter and the return. A concrete before/after
pair moved this model where an abstract rule did not — worth remembering if
the Mac client ever needs the same kind of correction.

**Separately:** the Analyze hotkey (F8) had a rule for illegible text — too
small, blurred, cut off mid-character — but nothing for a problem statement
that is legible and simply continues below the visible screen, which is the
ordinary case for a LeetCode-shaped question, not the exception. It could
write a final SOLUTION or FIX from a partial statement without ever saying
so. Added the same "say what's missing and stop" rule the voice path
already had, adapted to this template's own shape: a scrollbar showing more,
a constraints section that looks started but not finished, is now grounds to
answer `NEED: the constraints and the second example.` and nothing else,
rather than guess. Client-side, `ScreenAnalyzer.cs`, needs the Windows build
rebuilt to take effect — already done there; check whether the same gap
exists in whatever the Mac client sends for its own screenshot-only capture
path, since this only fixes Windows' copy of that template.

**Third fix the same day, same root cause as the one below it, worse in
practice: the user hit a real interview problem, asked for help 25 times, and
every single answer diagnosed the bug correctly in prose and then handed back
the exact same broken code. Fixed server-side, verified against the real
transcript's own code. Nothing for Mac to build, but the failure mode — a
model that states a diagnosis and does not apply it — is worth watching for
anywhere a description and the artifact built from it come from two different
calls.**

The problem was Insertion Sort List. The screen showed `ListNode
insertionSortList(ListNode head)` — a value-typed parameter dereferenced with
`->` throughout the body, which does not compile: "invalid argument type
'ListNode' to unary expression" on `!head`. Every one of 22 consecutive
retries answered CAUSE: "the signature uses ListNode instead of ListNode*",
then FIX: the identical `ListNode insertionSortList(ListNode head)`, never
once actually adding the pointer. Correct diagnosis, unchanged code, 22 times.

Two compounding causes, both closed:

**The Analyze hotkey (F8, no spoken question) has its own instructional
template, separate from the voice path fixed just below — CAUSE/FIX/SOLUTION
worked examples, no `"THE QUESTION:"` marker.** The fix below this one only
narrows the voice path; this template has no marker to narrow on, so
`actuallyAsked()` was falling back to returning the whole thing, same as
before that fix existed. Stage two then answered in CAUSE/FIX headings copied
from the example instead of its own SAY THIS/DETAIL format — visible proof
the injection was still live even after the first fix, just through the
other client template. Changed the fallback from returning the full prompt to
a short fixed instruction ("Analyze what is on the screen.") — there is no
real question on this path to extract, so stop pretending there might be one
to find.

**Even with that closed, the underlying instruction was too easy to satisfy
without actually applying it.** "Keep the exact class and method names the
problem gives" reads naturally as license to preserve the whole signature,
types included, and "match pointers and objects" describes the symptom
without saying which direction to fix it in. Rewrote both: names now means
identifiers, never types; a value-typed parameter dereferenced with `->` is
now named directly as the defect to fix, in both the parameter and the
return; and a closing instruction requires the code to actually differ at
the spot a defect was named, not restate the line under a different null
check. Verified against the transcript's own code, rendered back onto a
screen and sent through the real endpoint: this time the fix produces
`ListNode* insertionSortList(ListNode* head)`, correct in both places, on
the first attempt.

If a two-stage description-then-generation split exists on the Mac side
anywhere, the general shape to check for is the same: a model can be fully
capable of stating what's wrong and still fail to act on its own statement
when the instruction only describes the target shape rather than requiring
the before/after to visibly differ.

Asked "so which website is open in Chrome browser" while looking at an
ordinary pricing page, the app answered "I can see that Chrome is open to the
LeetCode Two Sum problem page" — a page that was not open anywhere. Not a
vague guess: a specific, wrong, confident fact.

Cause: `codingMessages` (stage two, `gpt-oss-120b`, which never sees the
image) was being handed the client's *entire* instructional prompt as "what
they asked" — hundreds of lines of formatting rules built by
`BuildScreenPrompt`, not the question. One of those lines is a worked example
teaching the model to name things specifically: `"the LeetCode Two Sum
page"`. A text model with no image and no way to tell an instruction from a
fact took the example as the fact.

This only became reachable because the dead-pipeline fix above brought stage
two to life for the first time. It was always contaminated this way; it had
just never run before today.

Two fixes, both server-side:

1. `actuallyAsked()` extracts just the words after the client's `"THE
   QUESTION:"` marker before forwarding anything to stage two. Falls back to
   the full prompt if the marker is missing, so older or other client
   payloads keep working exactly as before.
2. Stage one's extraction template can now say `"none: not a coding screen"`
   instead of being forced to fill in five headings when there is nothing
   coding-shaped to report. The call site treats that as equivalent to no
   result and falls through to the single-stage path, which already knows
   how to answer an ordinary question about an ordinary screen.

Verified against both shapes of the original failure, not just re-read: the
same full boilerplate prompt against a real IDE screen with a genuine
compile error no longer leaks the LeetCode example, and against a synthetic
non-coding dashboard page it now answers "I have the AIHubMix website open
in Chrome" and routes through the single-stage model, not the coding one.

If the Mac client ever forwards its own full prompt text as the "asked"
field into a second-stage call — check for it, because this exact shape (a
few-shot example indistinguishable from ground truth to a blind model) is
not specific to Windows' prompt, it is specific to handing a whole
instructional document to a model that cannot tell instruction from fact.

**Screen reading switched to Gemini, and a bug that made the coding pipeline
never actually run got fixed. Both live in the shared backend — nothing for
the Mac app to build, but the reasoning matters because it changes what a
screen answer is made of.**

Vision primary is now `gemini-3.1-flash-lite` via Google's OpenAI-compatibility
endpoint (`/v1beta/openai/chat/completions`), not the native `generateContent`
API — verified directly that it accepts the exact `{model, messages, stream}`
shape this file already builds for Groq, including image_url data URIs and the
`detail:"high"` field (Gemini ignores it rather than rejecting it, same as
Groq), and streams the same `data: {"choices":[{"delta":{"content":...}}]}`
shape `contentToken` already parses. Zero format translation needed.

Why switch: built a hard synthetic screen — file tree, an open editor, two
real compiler errors at named line numbers, a failing test with exact
expected/actual values, a build-output panel — and scored eight independent
facts a model would have to actually read, not infer. `qwen/qwen3.6-27b`
(the previous primary) recognised the problem shape and answered from memory,
missing the compile error sitting in red on the screen — the same failure
mode that produced the invented-career answers this file describes elsewhere.
`gemini-3.1-flash-lite` read all eight facts correctly in 1.46s. Also ~37%
fewer prompt tokens than Qwen and no per-minute ceiling, unlike Groq's free
tier. Fallback is still Groq's qwen model if the Gemini key is missing or its
call fails.

OpenAI dropped out of the automatic chain the same day: every model on that
key returns `"account is not active, please check your billing"`, confirmed
by calling it directly rather than inferred from a log line. The code path is
gone, not just unreachable — cuts a wasted round trip on every double-failure.
Restore it as a third tier if that account is ever reactivated.

**Separately, and worth knowing even though it's a pre-existing bug rather
than something this change introduced:** the two-stage "read the screen as
text, then have a coding-specialist model write the answer" pipeline
(`readScreenForCoding` → `codingMessages` on `gpt-oss-120b`) had been
returning empty text on every single call, for every provider, since it was
written. `contentToken()` strips the `"data: "` SSE prefix itself — its other
caller (`streamUnlessRefused`) passes the raw line and that's correct — but
`readScreenForCoding` was *also* stripping the prefix before calling it, so
the JSON got double-stripped and every parse threw and was swallowed. Every
screen-analyze request silently skipped the two-stage path and fell through
to the weaker single-stage one, where the vision model both reads the screen
and writes the code itself — which this same file's comments already
describe as unreliable ("asked to fix an LRU Cache, three attempts, three
different broken implementations"). Fixed by passing `line` straight through,
matching the one call site that was already doing it correctly. Verified live
against production: a real `/analyze-screen` request now streams from
`openai/gpt-oss-120b` (the coding-stage model) instead of the vision model,
and the fix it produced compiles and is correct — `import
java.util.concurrent.Callable`, `Exception` → `RuntimeException`, retry logic
untouched.

Both changes are deployed and live on the production backend as of
2026-08-22, tested end-to-end against the real `/analyze-screen` endpoint —
not just in isolation.

**Security audit, three fixes.** The screenshot stash on the backend could
exhaust the heap: two hundred images at up to eight megabytes each is 1.5 GB
against a 1.38 GB heap, and the count was global so one caller filling it
denied service to everyone else. Now two megabytes per image, four per
identity, sixty megabytes across everyone measured in bytes. Already live —
nothing for the Mac app to build, but worth knowing the shape of it.

**Never write personal details into the speech vocabulary file.** That file
is plain text, because the speech engine reads it directly and cannot decrypt
what everything else on disk is protected with — and it was carrying the
candidate's email address, LinkedIn URL and city, lifted out of their resume
and left unencrypted beside files that are not. Filter emails, URLs, phone
numbers and anything mostly digits.

It is an accuracy fix as much as a privacy one. A resume from Illinois put
"IL" in the vocabulary, and a vocabulary hint is exactly the nudge that turns
"I'll" into "IL" in a transcript. Two-letter capitals go. Be careful with the
domain test though: ".NET" is a framework the candidate may be asked about,
so only treat a suffix as a domain when something precedes the dot.

**Delete the live transcript file when the app closes.** Everything else the
app keeps is encrypted; that one is not, because the engine writes it, and it
held the last thing an interviewer said until the next launch overwrote it.
It is a scratch file for the turn in progress and has no value afterwards.

**The screen is used when the question is about the screen — not whenever the
screen is being watched.** This was written the wrong way round: everything
except a short list of personal questions went down the screen path. So "tell
me what is Java?" was answered by sending a photograph of the desktop: three
times the tokens, a worse answer, and the minute's allowance exhausted on a
question that never needed a picture. Watching became a tax on every question
rather than a feature for some.

Use the screen when the question says so — "solve this", "this error", "can
you see" — or when the previous answer came from the screen and this one
continues it, which is how "and what is the time complexity?" keeps working
after "solve this". Track that continuity with a flag set when the screen path
runs, not by looking for the word "screen" in the question: "can you solve
this?" does not contain it.

**Preparing screenshots only happens during an actual interview.** Making
screen answers the default quietly turned the two-second capture into a
screenshot every two seconds for as long as the app was open, uploaded
whenever the picture changed — which while somebody is working is constantly.
Around eleven megabytes a minute of their connection, and their screen.
Gated on the microphone having been used in the last five minutes: open the
app and leave it, and nothing is captured at all.

**And not at all on a machine that cannot hide the app from a capture.** The
fallback drops the window opacity to zero for ninety milliseconds. Once,
before an answer, that is invisible; every two seconds it is a flickering
window. Probe once and cache the answer — the probe sets the exclusion flag
and puts it back, so asking repeatedly churns the flag and leaves brief
windows where the app really is capturable.

**The compact overlay must strip code fences.** The main window lifts fenced
code into a monospace panel and stopped stripping fences so it could find
them. The overlay has no such panel and got the same text, so a candidate in
compact mode — used when someone is sitting across a desk — read their answer
with ```cpp printed through the middle of it. Strip the fence lines, keep the
code: in compact mode that window is the only place it appears.

**Collapse code in older history turns.** Every prompt carries the last few
turns, so a behavioural question asked after a coding one arrived at the
model with sixty lines of C++ attached, charged for on every request from
then on. The most recent turn keeps its code, because "can you optimise
that?" needs the thing being optimised; older turns keep "[code given]".

**Screen answers are on by default, and the toolbar button does the thing.**
"Watch screen" was a toolbar switch that started off every launch, so the
feature most likely to matter in a coding round was the one a candidate had
to remember to arm with an interviewer already talking. It is now a setting,
on by default and remembered. The toolbar holds the action instead: READ
SCREEN with F8 beside it, and pressing it reads the screen there and then.
The label no longer changes when pressed, because a control whose text
changes is read as a switch and this one is not.

**Nothing heard at all stops the microphone after 45 seconds, not 3 minutes.**
Three minutes is right for a pause inside a conversation. It is far too
patient for a session where not one word arrived, which is a key pressed by
mistake — and waiting three minutes to notice turned a stray press into a
notice that appeared over and over. Once anything has been said, the old
three minutes applies for the rest of that session.

**The idle notice goes in the badge, never in the answer.** It used to be
written into the answer panel, so an answer somebody was still reading was
replaced by a message about the microphone — and it fired exactly when they
were reading a long one with the mic still open.

**A rate limit is reported as a rate limit.** "The AI service is temporarily
unavailable" is true and useless: nothing is broken, this minute's allowance
is spent, and it returns on its own. The message now says so with a wait
time, and refuses to report a wait under fifteen seconds — an early version
printed "try again in about 1 seconds", which is worse than saying nothing.

**A 429 fails at once rather than three times.** Splitting the screen read
from the code writing added an attempt at the front, so a rate limit failed
through the read, an eight-second retry, and a fallback to the same model on
the same key. Sixteen seconds to learn what was known at the first response,
and it looked like a fault rather than a limit.

**Only a screen that moved counts as a second view.** A page with a live
"2,332 Online" counter produces different bytes every two seconds, so every
capture was kept as a new view and every question carried two pictures of the
same screen — twice the tokens for nothing. A coarse 16x16 greyscale
signature tells scrolling from a ticking counter.

---

---

## The speech engine is one file, shared, and it had forked

`speechmatics_engine.py` lives in the Windows repo and the Mac app depends on
it. They had drifted apart without either side being able to tell.

The Mac was running a compiled build of 1,074 lines against a source of
1,982 — from before `SARVAM_LANG_MAP`, before `--language`, before
`additional_vocab`, before the melia-1 rejection handling. Telugu arriving as
confident English nonsense on the Mac was a bug already fixed here months of
commits ago; the Mac simply could not reach the fix.

Three changes so one engine now serves both:

**`--sysfifo` exists.** It never did on this side — checked the full history,
not just HEAD, so it was not lost in a refactor. It was a Mac-only addition
living in a build nobody could rebuild. macOS cannot capture system audio in
a helper process at all: the helper does not inherit the app's
screen-recording grant, so the OS hands it silence rather than an error, which
is why it looks like a quiet room. The Mac therefore runs a CoreAudio tap
in-process and writes 16kHz mono s16le to a FIFO. The engine now reads that
FIFO as its system-audio source, presented as something with a `.read()` so
the mixer, the hot-swap logic and the level probes need no knowledge of it. A
writer that closes is reopened rather than treated as the end, because the app
may restart its tap between turns. Windows never sets it and opens a WASAPI
loopback as before.

**Single-dash long options are accepted.** The Mac passes `-mode both`,
Windows declares `--mode`, and argparse rejects the former outright — a shared
build would refuse to start on the Mac and look broken rather than
misconfigured. Normalised before parsing, so neither app has to change.
Unknown short flags are left alone.

**The device hunt is skipped when a FIFO is given**, or the engine would open
a loopback as well and mix the machine's own output into a feed that already
contains it.

**The app must be able to notice the engine going deaf.** The speech engine
can fail in a way that looks exactly like success: connected, reporting
online, microphone level moving, and no words ever coming back. Three separate
bugs in the audio reader produced precisely that shape, and every one was
found by a person noticing an empty transcript rather than by the app noticing
anything.

That is the failure a user can neither diagnose nor work around. The app looks
healthy, so they assume they are doing something wrong, and the interview is
over before anybody works out otherwise.

The engine already says enough to catch it: it prints when the microphone
hears speech, and it prints how many characters came back. Speech arriving
with nothing returned for twelve seconds is the signature. Either line alone
is worthless — silence is normal and so is a lull — but the pair is decisive.
Warn at most once a minute, or the warning buries the answer it is warning
about.

**Session refusals fail closed now, and there are tests beside the engine.**
`quota_exceeded` — reachable simply by having both apps open, since the
account has a concurrent session limit — matched nothing, so it retried
forever behind a UI saying "connecting". Exactly the blocked-contract failure
we had already fixed once, arriving through a different string.

Adding strings one at a time loses that race by construction: every server
refusal nobody has met yet becomes an infinite retry. So the default is
terminal and the transient cases are enumerated instead. `not_allowed`,
`quota`, `forbidden` and 403 all exit as auth failures, which makes the app
drop its cached token and refetch — right whether the account was blocked,
over quota, or the key replaced, and it recovers by itself when the condition
clears.

`tests/test_engine_contract.py` runs anywhere and checks the promises both
apps are built against: single-dash normalisation, the three modes, sysfifo,
both key variable names, APP_DATA_DIR, fail-closed refusals, Sarvam routing,
the melia downgrade, the max-delay floor. `tests/test_fifo_stream.py` needs a
kernel FIFO and skips on Windows with a message saying so — run it on macOS or
Linux before merging any change to the reader.

The rate assertion is the one that matters and generalises past this file:
anywhere data is synthesised to fill a gap, assert the rate and not only the
content. "Produces silence when quiet" is true at 1x and at 470x alike, which
is why two rounds of correctness testing missed it.

**The FIFO reader has to be paced like an audio device, not read like a file.**
This is the bug that replaced the one below, and it was worse. A read on a
quiet FIFO raises EAGAIN, and answering that by manufacturing a chunk of
silence and returning immediately means the caller loops and silence is
produced as fast as the CPU allows. Measured on Mac at 141,980 silence chunks
— 14,198 seconds of audio — in a thirty second run against twelve seconds of
real speech. About 470x realtime, burying the speech at 400:1, and the
transcript came back empty.

Worse than what it replaced, because it broke the normal path rather than the
edge case: a session that never cycled its writer still worked under the old
bug, and nothing worked at all under this one.

A device hands over 1600 frames every 100ms and blocks in between. A FIFO
hands over what it has and says EAGAIN, so the waiting a device does for free
has to be done explicitly. select() on the fd with the remainder of the chunk
period does it: it returns the instant data arrives and costs nothing while it
does not. Every path out of read now takes about one chunk duration — audio,
silence, quiet writer, no writer at all.

The test that catches this is rate, not behaviour. "Does it produce silence
when quiet" passes at any speed, which is why two rounds of state-machine
testing missed it. Feed N seconds and assert the reader consumes about N
seconds.

**The FIFO reader survives a writer that comes and goes.** The first version
died the moment one closed, which on macOS is a normal event rather than an
edge case: the CoreAudio tap stops and starts while the engine keeps running,
and every mode switch restarts it. Measured on Mac at 85,895 lines of "I/O
operation on closed file" in fourteen seconds, never recovering, with the
transcript gone for the rest of the session.

Two faults compounded. The reopen closed the handle and then called a blocking
open() on a FIFO with no writer, which does not return; and the closed handle
was left in place, so every later read raised on it rather than retrying. The
object was permanently broken while looking alive.

Opened O_NONBLOCK now, so nothing ever blocks. A read raising BlockingIOError
means a writer is attached and quiet — silence for the gap, handle kept. A
read returning empty means every writer has gone — let the handle go and
reattach on the next read. Those two cases are exactly what needs telling
apart and the kernel already distinguishes them, so no timers are involved.

Reattaching is attempted on every read that finds no handle, not on a timer: a
half-second throttle was costing half a second of audio each time the tap
cycled, and the tap cycles on every mode switch, so that is the interviewer
speaking rather than idle time. Only the logging is throttled, to once every
five seconds.

**Three Windows-shaped assumptions removed**, so the engine is genuinely
platform-neutral rather than portable-if-you-squint. All three were found by
the Mac session running it, not by reading it.

`APP_DATA_DIR` is honoured when set, falling back to `LOCALAPPDATA`. That
variable is Windows-only, so on macOS the path fell through to a temp
directory: latest.txt, pause.flag and reset.flag written to /var/folders while
the app polled Application Support. The engine would run perfectly and the app
would show an empty transcript forever, with no error at either end. A failure
invisible from both sides is worse than a loud one.

`SPEECHMATICS_API_KEY` is accepted as well as `SM_API_KEY`. Two names for one
thing because of which was typed first, and renaming either would break the
other side for nothing.

`--mode mic` exists again. macOS falls back to it when its CoreAudio tap is
refused permission or crashes mid-session. Dropping it turned a
degraded-but-working fallback into an engine that refuses to start, which is
the worst direction for a fallback to fail in. In that mode system audio is
not probed at all, since the whole premise is that it cannot be captured.

**Telugu does not work on Windows either, and the reason is not the engine.**
The Sarvam path reads `SARVAM_API_KEY` from the environment, the Windows app
passes it from config, and that config field defaults to empty with no UI to
set it. So it is empty on every install. The engine exits with "SARVAM_API_KEY
not set" rather than transcribing. Whoever adds a key needs to add it to both
apps and to the settings UI; the shared engine is not the missing piece.

**What still needs deciding, and it is not a code question:** this engine has
no owner and no versioned artifact. The compiled binary lives in neither
repo, survives only in local build folders, and a clean clone of the Mac repo
cannot produce a working app — it has the `.spec` but not the `.py`. Both
sides can silently run different engines and neither can tell. A tagged
release built for both platforms would fix that; copying a binary between
machines will not.

## Where the reasoning lives

The Windows commit messages, `git log` on `windowsNative`, one commit per
fault, each explaining what broke and why the fix is shaped that way. That
history is the point: several bugs today were caused by earlier fixes, and
without knowing why a number was chosen the next session will change it back.

`MAC_PARITY.md` in the same repo is the older feature-gap list and is
**unverified** — it was built by grepping Windows class names against a stale
Mac copy. Treat it as a prompt for questions, not as fact.
