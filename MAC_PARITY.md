# Bringing the Mac app up to the Windows app

The Windows client moved 71 commits between 2026-08-13 and 2026-08-18. The Mac
client's last change is 2026-08-13, so everything below is missing from it.

**How to use this on the Mac machine.** Point the assistant at this file and at
the Windows repository, then work through the sections in order. Every entry
names the behaviour and why it matters rather than the Windows code, because
the Mac app is a different language and toolkit and a translation of the
implementation is not what is wanted there. A translation of the behaviour is.

```
Windows repo: https://github.com/drBindu/replysis-windows
This file:    MAC_PARITY.md
Mac repo:     https://github.com/moto123a/interview-copilot-mac
```

The Windows commit messages carry the reasoning for each change.
`git log --since=2026-08-13` is the full list, and each message says what was
wrong before, which is usually the part worth keeping.

---

## Already done for Mac, no work needed

Both clients talk to the same backend, so these are live for Mac users now:

- **Screen reading is accurate.** Vision requests always send `detail: high`.
  It used to be attached only when the client asked for the "openai" provider,
  and both clients ask for "groq", so screenshots were being skimmed.
- **A refusal never reaches the user.** If the model answers "I'm sorry, I
  can't help with that", the server discards it, asks again in plainer words,
  and sends only the real answer. Both the text and the vision paths.
- **Refusals are not charged.** They used to count as a delivered answer.
- **Answers start immediately.** `include_reasoning: false` and
  `reasoning_effort: low` on gpt-oss, which was thinking invisibly first.
- **Resume analysis and tailoring no longer truncate.** Reasoning was eating
  the token budget those services were sized for.

Nothing to port. Verify by testing the Mac app against production.

---

## 1. Bugs that break a live interview

Port these first. Each fails silently, which is why none showed up in testing.

**The sign-in token expires after an hour and nothing refreshed it.** An app
opened before an interview, or an interview running past the hour, sent an
expired token, got a 401, and the 401 handler treated that as a sign-out: plan
gone, guest credits, no answer, mid-interview. The refresh token was on disk
the whole time. Refresh before every request; it costs nothing when the token
is still valid.

**Transcription can die without anything noticing.** The "engine online" flag
was set true on first connect and never cleared. A dropped session or a crashed
engine left the app believing speech still worked while the user spoke into
nothing and got an empty question back. Clear it when the session drops and
when the process exits, and show it.

**One dropped packet lost the question.** A single failed request produced
"check your connection and try again", which nobody can act on mid-interview.
Retry once, 250ms later. Safe at that point because nothing has streamed, so
the server has already refunded the credit.

**The app got slower the longer the interview ran.** Each answer decrypted the
whole transcript, appended, and re-encrypted it, on the UI thread. Question 40
stalls far longer than question 1. Move it to a background worker with ordering
preserved.

---

## 2. Screen analysis

Rewritten end to end on Windows. In rough order of impact:

**Capture the window in front, not every monitor.** The old code stitched all
monitors into one image and squashed it, so text arrived at two thirds size on
two screens and half on three. Vision models shrink whatever they receive until
the shortest side is 768px, so a full screen is read at 1365x768 regardless.
Sending one window puts the pixels where the question is.

**Send PNG, not JPEG.** A screenshot is text on flat colour, the exact case
JPEG handles worst. Its ringing around letter edges is the difference between
reading `l` and reading `1`.

**Do not upload more than the model reads.** Cap the shortest side at 768. On a
4K screen that is an eighth of the bytes for an identical result.

**Do not hide the window to take the shot.** Mark it excluded from capture
instead, so it stays on screen and simply is not in the copied pixels. Hiding
and waiting for the compositor reads as the app blinking on every capture.

**Answer the spoken question, not the screen.** When the question arrives by
voice the reply has to be the sentence to say out loud. A description of the
window is accurate and useless to someone who must speak in two seconds.

**Say which window was read.** An answer about the wrong window is otherwise
indistinguishable from a bad answer about the right one.

**Record what is on screen, not just the answer.** A hidden `SCREEN NOTES` line
listing window name, menu labels, buttons and headings, kept as context and
stripped before display. Without it a follow-up about the navigation bar gets
answered with whatever the first answer happened to mention.

**Detect questions that are about the screen** ("take a look at this", "solve
this", "what do you see") and capture automatically. Add a mode for when the
interviewer is sharing their screen that reads it for every question.

---

## 3. Answer quality

**Two-part answers.** The spoken answer first, at the length the question
deserves. Then `MORE TO SAY` and 4 to 6 bullets of what could be added if
pushed. Users said answers were too short; the answers were also correct, so
the fix was somewhere to go rather than more words up front. Skip it for
greetings and one-sentence logistics.

**Sound spoken, not written.** "Java is a statically typed, object-oriented
programming language that runs on the JVM" is an encyclopedia entry read aloud,
and an interviewer knows it immediately. Contractions throughout, sentence
lengths that vary, one idea per sentence, no triple adjective lists. Ban and
replace: leverage, utilize, robust, seamless, comprehensive, delve, myriad,
facilitate, pivotal, showcase, "is known for", "plays a crucial role".

**Never claim to have said something that was not said.** "As I mentioned" when
nothing was mentioned reads as evasion to the one person who knows exactly what
was said. On Windows the prompt was actively teaching that phrase for drill-down
questions, and it leaked onto everything else.

**Keep the prompt small.** Prompt size buys latency directly: measured 0.25s to
the first word at 400 bytes against 0.50s at 6.4KB. Windows had two system
prompts and was sending the 12,742-character one in its default mode while a
2,138-character one sat unused. Check whether the Mac app has the same split.

**Do not send what the server ignores.** The raw resume was uploaded with every
question and dropped on arrival, because the messages array already carries the
curated facts.

---

## 4. Speech

**Do not prime the recogniser with the wrong vocabulary.** The Windows
`additional_vocab` carried a previous user's analytical chemistry terms: HPLC,
FTIR, GMP, ALCOA, LIMS, OOS, OOT, CAPA. Short ones like OOS and GC get
substituted for ordinary speech. Check the Mac list for the same leftovers.

**max_delay 0.7, not 0.85.** 0.7 is the documented floor, and the extra 150ms
is paid on every sentence.

**Never select an audio device that does not answer.** A silent device and a
frozen device both measure zero amplitude, and committing to a frozen one takes
system audio down for the session. Test whether the device responds, separately
from how loud it is.

---

## 5. Distribution

Windows updates itself through Velopack, staged in the background and applied
on exit so an interview is never interrupted. Mac ships a .dmg from GitHub
Releases with no update path at all. Sparkle is the usual equivalent.

---

## What to do first

1. Section 1 in full. Those are live-interview failures.
2. The first four items of section 2. That is most of the screen-analysis gap.
3. Section 3, which is prompt work and needs no new plumbing.
4. Sections 4 and 5 when there is time.
