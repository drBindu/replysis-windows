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

## Where the reasoning lives

The Windows commit messages, `git log` on `windowsNative`, one commit per
fault, each explaining what broke and why the fix is shaped that way. That
history is the point: several bugs today were caused by earlier fixes, and
without knowing why a number was chosen the next session will change it back.

`MAC_PARITY.md` in the same repo is the older feature-gap list and is
**unverified** — it was built by grepping Windows class names against a stale
Mac copy. Treat it as a prompt for questions, not as fact.
