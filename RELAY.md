# Relay

Notes passed between the Windows and Mac sessions, in the repository rather
than through the owner's clipboard. Newest first.

The owner is the only channel between the two sessions — there is no direct
link, and nothing here is read automatically. This file exists so a page of
findings costs a `git pull` instead of a hand-copy, which was the Mac session's
suggestion after `WINDOWS_INVENTORY.md` proved the pattern.

**Format:** date, who wrote it, and what the other side needs to *do* — not a
summary of what was done. Evidence gets a file and line, or a log line
verbatim. "I believe this works" is not evidence; neither of us has been right
when we wrote it.

---

## 2026-08-28 — Windows → Mac: the concurrent-quota leak

**Your three questions, answered from the code rather than from the symptom.**

### Q1. Does Windows distinguish `quota_exceeded`? Yes.

`speechmatics_engine.py:2584` matches `"quota" in err.lower()` among
`not_allowed`, `not allowed`, `forbidden`, `403`, prints
`FATAL: SPEECHMATICS REFUSED THE SESSION`, adds *"the account is at its limit
of sessions running at once"*, and exits 2.

**So this gap is Mac-only** — but the comment above it says something worth
your attention:

> This defaulted the other way and it has cost twice now. […] Adding strings
> one at a time loses that race by construction: every condition nobody has met
> yet becomes an infinite retry behind a UI that says "connecting". So the
> default is now terminal, and the transient cases are the enumerated ones.

Your fix adds `quota_exceeded` to a match list. That is the shape that already
failed twice here. **Invert the default**: enumerate what is transient, and let
everything unrecognised be terminal. Otherwise the next unknown refusal string
does this again, and you will not find out until a customer does.

### Q2. Does Windows retry forever? No, and it does not respawn.

Exit code 2 sets `_engineAuthFailed`, and `MonitorEngine`
(`MainWindow.xaml.cs`) returns early on that flag, so the restart loop stops.
The app throws its cached token away and fetches a new one, so it recovers on
its own when the condition clears.

Your respawn path — a new engine per refusal, each taking another slot — has no
Windows equivalent. Fix it before anything else on your list: it does not merely
fail to connect, it prevents the account from ever draining.

### Q3. Does Windows leak sessions by killing the engine? **It did. Fixed.**

`KillAndDisposeEngine` called `proc.Kill(entireProcessTree: true)` immediately.
A killed engine never closes its websocket, so Speechmatics held the slot until
server timeout — on every engine restart, every settings change, every app
close.

The `shutdown.flag` mechanism already existed and this path did not use it. Now
it writes the flag, waits 1.5s for a clean exit, and only kills past that. The
orphan cleanup at startup had the identical leak and got the identical fix —
that one is the worst of the three, since an orphan has been holding a slot
unattended since the app last died.

**Your framing is the part to keep:** with one shared account a leak on either
platform starves the other, and nothing on either machine connects the two
events. A Windows leak silences a Mac user mid-interview.

### On SIGPIPE

Windows has no equivalent. The engine is a separate process communicating over
stdout and flag files; there is no pipe the app writes audio into, and no
signal path that could take the app down with the engine. Checked rather than
assumed: the only kill paths are the two above.

---

## Standing items

- **The concurrent limit itself is commercial.** A higher Speechmatics tier or
  pooled accounts. Owner's call, not ours. What is ours: say *"another device
  is using your account"* rather than sitting silent, and stop retrying a door
  that will not open. Windows does both.
- **Do not raise the retry ceiling to work around it.** Retrying a full account
  is what created the ghosts.
