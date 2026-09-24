# Windows reliability fixes — 2026-09-24

## Scope and state

Project: `C:\Users\krish\Desktop\windowsNative`, branch `main`, based on `5ad20ba`.
The owner approved committing and pushing this reliability batch. No installation, deployment or release is part of this handoff.
No pricing, billing, credit allowances, metering rules, model choices, website or backend code changed.
Existing glass, opacity and transparency settings were not redesigned.

## Implemented

- Recording cap calculated from the current audio chunk size: 90 minutes instead of approximately 35 minutes. Settings now states the limit.
- Recording open/write/close failures create a failure marker, not a success marker. Windows recognizes failures and warns when starting the next session; a missing engine no longer implies success.
- Authentication refresh responses are applied only to the identity generation that requested them. Logout and new login invalidate older results atomically.
- Cloud session fetches and sync completions reject stale identity generations; the panel clears on account changes.
- Screen analysis receives the interrupt cancellation token. Cancelled/replaced answer requests cannot reset newer request state. Account changes cancel active answers.
- Answer streams use cancellable asynchronous reads with a 45-second idle timeout; no synchronous EndOfStream probe can block cancellation.
- Session answers and duration finalization use one ordered background writer. Shutdown waits up to five seconds for queued transcript writes.
- Practice suggestion measures initial silence from listening start even when no first transcript arrives.
- Automatic-turn diagnostics no longer log question excerpts or full continuation questions. Existing historical logs were not deleted or scrubbed.
- Plain global Space is off by default. Ctrl+Alt+Space toggles manual listening from other apps. Space continues to work in Replysis, including its compact window; Settings allows opting back into the legacy global behaviour.
- Session deletion explicitly says "Delete local copy" and warns that cloud backups remain. No new cloud-delete endpoint was invented.
- Optional Windows system-proxy support for the shared HTTP clients, with restart required. Speech-provider proxy support is not promised by this option.
- Release workflow depends on the reusable verification workflow for the same source revision, including the new recording tests.

## Verification

- Release app build: zero warnings/errors.
- All 23 C# harness suites pass. New tests cover identity invalidation, ordered writes, encrypted final-answer persistence, bounded drain, initial silence, safe shortcut decisions, stream EOF, timeout and cancellation, plus the second-pass cases below.
- Python engine contract and output pipeline checks pass.
- Four new Python recording tests pass, executing the production recording routines without audio/network access. Cover duration math, valid closed WAV before success, open failure, write failure and close failure.
- Python syntax compilation passes.
- Bundled speech engine rebuilt locally; --help smoke test succeeds. Build banner source hash: `a078c15fac44` (Git-normalized source blob prefix).
- Workflow YAML syntax parsed locally. GitHub-hosted release verification has not been run.

## Before public release

### Second audit pass completed September 24

Additional local fixes:

- Closing the login window cancels its HTTP requests and OAuth callback reads. Completed sign-in is marked successful before the welcome animation. Callback parsing has a 16 KB bound, validates state, method and path, and rejects duplicate parameters and incomplete requests.
- Resume extraction runs off the UI thread, enforces the 10 MB limit while reading, rejects oversized DOCX expansion, and discards results after account changes, window closure or conflicting edits. Third-party PDF parsing is not a security sandbox.
- Dragging a compact window larger than its work area no longer supplies invalid bounds to Math.Clamp. Actual multi-monitor/DPI behaviour still requires visual testing.
- New sessions clear screenshot references, screen follow-up budgets and automatic-question continuation/deduplication state. Late prepared-shot responses cannot repopulate the next session. Account changes clear displayed answer history, and shutdown cancels active AI work and stops the audio-source timer.
- Store version stamping updates only the manifest Identity version and the app assembly versions. The old broad replacement could corrupt dependency MinVersion attributes. The manifest now declares Windows Desktop 10.0.17763.0 as its minimum, matching the project, instead of an additional Universal-family target.
- Store packaging now depends on verification. Native build commands stop on failure rather than potentially accepting stale engine output. StoreContext is cached only after successful window initialization; the package-identity P/Invoke return type now matches a Windows LONG.

Final local verification after these changes: Release build 0 warnings/0 errors; all 23 C# suites pass; 4 recording tests pass; engine contract and output pipeline checks pass; 2 Store version-stamping tests pass. The stamping tests use temporary copies, not the real project version. All 3 workflow YAML files parse; git diff --check passes (line-ending notices only).

The added C# suite exercises resume read limits and DOCX expansion, oversized-window bounds, and OAuth state/header/cancellation cases. It does not simulate the complete Google sign-in flow, a live interview or Store installation. NuGet reported no known vulnerable dependencies; an OSV query for the 24 installed engine-build-environment packages returned no advisory IDs. These are advisory checks, not a security certification.

### Mandatory-update policy — owner-approved fail-open behaviour

The current Store check permits launch on timeout/error, and the update UI permits continuing after an unsuccessful installation. The owner explicitly approved retaining this behaviour so a Store outage or poor connection does not prevent an interview. It is not an unresolved demand for a stricter gate, and this batch does not change the policy. It is also not an unconditional mandatory-update guarantee. Its installation timeout stops waiting on the task without explicitly cancelling the underlying Store operation; whether installation continues must be checked in a real packaged update test before treating a timeout as safe to begin an interview.

Do not advertise instant forced updates or mark that requirement verified. Test an older Store-installed version against an available mandatory newer version, including offline, slow-download, failure, retry and restart paths. No package was submitted, installed, published or released in this audit.

### Remaining manual validation

These automated checks are not live-call or installer certification. Verify the packaged app in 60-minute Meet, Zoom and Teams calls; speakers/headphones and switching output devices; screen-analysis interruption; sign-out during slow requests; immediate close after an answer; and recording longer than 45 minutes.

Test actual Settings layout, compact-window shortcuts, multi-monitor/DPI behaviour, clean install and updates. Test a real required-proxy network separately, including STT connectivity.

A failed or exceptionally slow disk can still prevent persistence; the five-second shutdown deadline is intentionally bounded. Recording recovery can retain an unencrypted raw WAV when encryption fails (pre-existing behaviour). Historical debug logs may still contain old transcript text. Do not claim all historical local data was scrubbed or that all storage/network failures are solved.

The development build is under `bin\Release\net8.0-windows10.0.19041.0\InterviewCopilot.exe`. The installed production copy was not replaced or launched.

Do not touch live pricing/billing work owned by the other agent. Commit/push of this batch is owner-approved; publishing a release is not. After push, hand Windows back to Claude and do not begin another Windows edit batch until ownership is clear.
