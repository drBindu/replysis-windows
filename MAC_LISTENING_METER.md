# Listening time: what the Mac app still needs

Windows, the website and the backend are done. The Mac app is the last client,
and until it reports, its users listen for free while everyone else is metered.

## Why this exists

Credits count questions. Speechmatics charges by the hour of audio. Those two
were never connected, so the expensive half of the bill was invisible: a
microphone left open all afternoon cost real money and showed up nowhere.

It is worse than it sounds. A Max plan is 5,000 credits, which is 1,000
questions, which at the usual twenty questions an hour is about fifty hours of
audio, which is roughly the price of the plan before Stripe's cut. It survived
only because most people never finish what they bought.

## The backend is already live

Nothing to build server-side. Two things exist:

**The gate.** `GET /api/v1/stt/key` now returns `402` with
`{"reason": "audio-limit"}` once the month's allowance is gone. No token means
no audio, so this holds even for a client that never reports.

**The meter.** `POST /api/v1/usage/listening` with `{"minutes": N}`, and the
same `Authorization: Bearer <firebase id token>` and `X-Device-Id` headers
every other call already sends. It replies:

```json
{ "usedMinutes": 128, "allowanceMinutes": 1800,
  "remainingMinutes": 1672, "isUnlimited": false, "plan": "max" }
```

`remainingMinutes` is `-1` when the plan is unlimited.

Allowances, which must not be redefined on the Mac side: free 60, pro 900,
max 1800, lifetime 1800, teams 6000. They live in `FirestoreCreditsService`
and in the website's token route, and all of them write the same
`audioMinutesUsed` field on the same user document. Somebody using the Mac app
and the website has one allowance, not two.

## What to build

**Report as you listen, not at the end.** Every minute of active listening,
POST one minute. An app that is force-quit, a lid that closes and a connection
that drops all look identical afterwards, and all three would otherwise be
free. On stop, flush anything above thirty seconds as one minute rather than
rounding it away, or an interview made of short turns costs nothing.

**Stop the microphone after three minutes of silence.** Most of the waste will
never be anyone being greedy, it will be somebody who opened the app and went
to lunch, and an empty room bills the same as an interview. Reset the timer on
any transcript arriving, not on audio level, so background noise cannot hold a
session open.

Say so when it happens. Stopping silently is worse than the waste it prevents,
because someone coming back would speak into a microphone they believed was on.
Windows shows:

> The microphone switched off after 3 minutes of silence, so your listening
> time is not spent on an empty room. Press Space to start again.

**Warn before the cap, not at it.** Windows surfaces a notice at fifteen
minutes remaining. Transcription stopping without warning mid-interview is the
worst possible way to learn a limit exists.

**A reconnect resumes the meter, it does not restart it.** Otherwise a dropped
connection resets the idle timer for free.

## The reference implementation

`MainWindow.xaml.cs`, the `LISTENING TIME` section: `StartListeningMeter`,
`StopListeningMeter`, `ListeningMeterTick`, `StopForIdle`,
`ReportListeningMinutesAsync`, `WarnIfListeningTimeLow`. The website's version
is in `app/real-interview/_lib/stt-client.ts`, and is closer in shape to what
the Mac app will want if it is event-driven.

## One thing to get right

Never fail an interview over accounting. Every reporting path on Windows and
the website swallows its errors: a failed report loses a minute, never the
call. The gate is the thing that protects the money, and it is server-side
already.
