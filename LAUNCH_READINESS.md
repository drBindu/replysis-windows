# Launch readiness, 2026-08-18

## Will it crash in the middle of an interview

The honest answer is that it is very unlikely to crash, and that crashing was
never the main risk. Every serious problem found in the last two days failed
**silently** instead: the app looked fine and did the wrong thing. Those are
worse, because the user cannot report what they cannot see.

**Crash protection.** Three handlers are installed: dispatcher, unobserved
task, and app domain. The recovery policy is a deny-list, not an allow-list,
which is the right way round: only genuinely unrecoverable faults close the app.

```
Fatal:      OutOfMemory, SEH, AccessViolation, BadImageFormat,
            TypeLoad, MissingMember
Recovered:  everything else
```

So a null reference in a timer, a parse failure, a UI element that vanished,
none of them end the interview. Before this was inverted, a common exception
closed the app mid-answer.

**Timers.** Several tick handlers have no try/catch, but they only move labels
and toggle visibility, and anything they throw lands in the recoverable path
above. Not worth changing.

**Transcripts survive a crash.** They are streamed to disk as they arrive, not
written at the end.

---

## What was fixed in the last two days

Nine real bugs. Five only appear during a real interview, which is why testing
at a desk never found them.

| Bug | What the user experienced |
|---|---|
| Sign-in token expired after 1 hour | Plan lost, guest credits, no answer, mid-interview |
| Transcription died on a dropped connection | Spoke into nothing, no error shown |
| Transcription died on a crashed engine | Same, different cause |
| A refusal was shown as the answer | "I'm sorry, I can't help" while waiting to speak |
| One dropped packet lost the question | "Check your connection", which nobody can act on |
| Two region pickers could stack | Two full-screen overlays over the interview |
| Charged twice for a refusal | Money |
| Charged at all for a refusal, always had been | Money |
| Slower with every question asked | Question 40 stalls, question 1 does not |

Plus roughly 2.3 seconds of delay removed, none of it from the AI.

---

## What the app uses

**Everything expensive is server-side. No paid keys ship in the app.**

| Purpose | Service | Model |
|---|---|---|
| Live answers, default | Groq | `openai/gpt-oss-20b` |
| Live answers, "Accurate" | OpenAI | `gpt-4o` |
| All screen analysis | OpenAI | `gpt-4o`, `detail: high` |
| Resume analysis | Groq | `openai/gpt-oss-20b` |
| Resume tailoring | Groq | `openai/gpt-oss-120b`, falls back to `gpt-4o` |
| Speech to text | Speechmatics | `enhanced`, `max_delay 0.7` |
| Speech, unsupported languages | Sarvam | |
| Sign-in, credits | Firebase | |
| Payments | Stripe | live mode |

**The only key inside the app** is the Firebase client API key, which is public
by design and identifies the project rather than granting anything. Groq,
OpenAI, Speechmatics and Stripe keys live only on the server. The speech key
the app receives is a temporary token valid for one hour, not the account key.

**Endpoints the app talks to:** `replysis.com` (own backend), Firebase identity
and Firestore, Google OAuth, Speechmatics token minting, and the GitHub
releases feed for updates.

---

## Where it can still go wrong

**Speech has a floor of 0.7 seconds.** That is Speechmatics' documented
minimum, not a setting left untuned. Roughly two thirds of the wait between a
question ending and an answer appearing is this. Deepgram runs at about 300ms
and would be a real improvement, and also a rewrite of the part of the app that
hears the interviewer. Not before launch.

**System audio depends on the user's hardware.** Devices that accept a stream
and never return audio are common, and which ones are real differs on every
machine. The app now refuses any device that does not answer and says so
plainly when none do. On the test machine the real output is a TV over HDMI
whose loopback never responds, so Interview Auto cannot hear it. Some users
will hit this.

**Unsigned direct download shows a warning.** Not applicable while the Store is
the only channel. It becomes relevant when the .exe ships.

---

## What to do, in order

**Before telling anyone about it**

1. Run one real mock interview end to end. Watch mode, a coding question, a
   behavioural question. This is the only test that exercises what users do.
2. Check the `[SPEED]` line in the debug window (F12) to see the real wait on
   a normal connection.
3. Confirm the Store submission passes certification.

**First week**

4. Watch the credits and error logs on the server daily. Nine bugs surfaced in
   two days of real use; more will surface in a week of other people's.
5. Buy code signing (~$10/month) before the .exe channel opens, so the
   reputation clock starts as early as possible.

**When there is time**

6. Ship the .exe channel. Store review is days; the .exe is minutes, which
   matters when a fix is urgent.
7. Evaluate Deepgram against real interview audio, not benchmarks.

---

## The honest summary

The app is in far better shape than it was two days ago, and the things that
were wrong were the kind that only appear in front of a real interviewer.

It is not proven. Nine bugs in two days came from one person using it, and
none were found by reading code alone. Expect more, keep the fix path short,
and treat the first week as testing that happens to have users attached.
