# Release check, 2026-09-28 (Windows 1.0.25)

Scope: the uncommitted working tree on top of 1.0.24, mostly the Setup and
Interview steps, the sign-in window, Finish, and log privacy. Follows
`RELEASE_AUDIT_2026_09_27.md`, whose blockers are re-checked below.

## Ran and passed

- Release build: 0 warnings, 0 errors. `dotnet list package --vulnerable`: none.
- `tests/CleanerTests` (C#): all suites passed.
- Python: `test_engine_contract`, `verify_output_pipeline`, `test_recording`,
  `test_store_packaging`, `test_audio_samples`, and `py_compile` of the engine.
  `test_fifo_stream` is macOS/Linux only and was not run.
- The app launches, the window title is "Replysis AI", and the process stays
  responsive four seconds after start.

## Found and fixed in this pass

1. **Watch-screen default had been flipped to off** (`AppConfig.WatchScreenEnabled`,
   its Settings text, and its test). That reverses the owner's 2026-09-17
   decision that continuous capture is intentional. Restored to on. What stays
   from the same edit: capture only starts after an interview begins, and is
   stopped on Setup, Past Sessions and Finish (Finish used to leave the timer
   running behind Past Sessions).
2. A middle dot in visible text ("PDF · DOCX · TXT") on the resume drop zone.
   Now "PDF, DOCX or TXT".

## Closed since 09-27

- Blocker 2 (Practice mode paused capture while an answer streamed): fixed in
  `3570bdc`. Still wants a live session where the interviewer talks over the
  candidate reading an answer back.

## Still not verified

- Physical microphone unplug/replug, default-device change, sleep/resume, and
  token expiry mid-interview. Nothing here exercised real hardware.
- A clean-account install of the signed direct installer.
- Long pauses (3-4 s) and more than two continuations in Auto mode.
- The Setup/Interview slide was reviewed in code and by the owner's screenshots,
  not by an automated UI test.
- The backend billing-race fix (`InterviewController.java`, `AnswerBillingTests.java`
  in `uiii/backend`) is uncommitted and NOT deployed. The client works without
  it; the refund race it closes stays open on the live server until deployed.
  Client error text no longer promises "No credits were used" for that reason.

Do not describe this build as risk-free.
