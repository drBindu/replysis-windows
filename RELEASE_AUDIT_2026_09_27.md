# Release assessment — 2026-09-27

Status: **Approved for a controlled private test; not yet approved for public release.**

## Additional verification

- Repeated verification after the latest request: Release build succeeded with zero warnings/errors; CleanerTests completed with `all passed`; audio samples, engine contracts, packaging fixtures and output pipeline checks passed again.
- With the television paused, a clean live loopback question was transcribed in full, submitted once, answered once, and did not retrigger in the following silence. The first answer token arrived 769 ms after submission. The test consumed 5 credits on the Max plan.
- A second live test deliberately split one question across a 1.4-second speaking pause. It reproduced a double answer because the provider inserted a question mark after the first clause. Punctuation can no longer bypass a 2.8-second continuation grace period. Repeating the same split test produced one 99-character transcript, one request and one answer; no second request appeared during the following silence. The first answer token arrived 1.837 seconds after submission on that run.
- Empty Auto restarts no longer erase the completed question from the Interviewer panel. The old question remains until non-empty words for the next turn arrive; Manual still clears on its explicit new turn. Regression tests cover all three states.

- Ran `verify_engine_contract.py`: all client/engine log formats and CLI arguments pass, including invoking the engine's help parser.
- Ran `test_store_packaging.py`: both fixture-based version-stamping tests pass. This does not install or launch an MSIX.
- Ran `verify_output_pipeline.py`: output formatting checks pass.
- Built a fresh signed 1.0.24 private-test MSIX bundle. Its embedded engine and `engine-dist` both have SHA-256 `C2E8A13CD82D291D68EC4A386BDFC69B60992E9F8A51A3251B18812834C49C86`.
- Rebuilt after the final Auto/UI fixes at `build/release-final-auto/InterviewCopilotPackager_1.0.24.0_Test/InterviewCopilotPackager_1.0.24.0_x64.msixbundle`. Signature status is Valid (`CN=krish`); bundle SHA-256 is `99801551F565C09791E2BF163BF2E4B7389FF653015FB314E7AA0FC569A1C49A`. The embedded engine hash matches `engine-dist`.
- Inspected the existing 1.0.23 MSIX bundle without installing it. Its engine hash is `FCEC8CCE85AD3444655A6DDAAEA3D53DBCC4479A90F78B3A9E721E1DB2A193A6`, different from the corrected local engine. That installer is not the corrected test build.
- The existing deaf-engine stub emits a microphone signal without an amplitude; the current client requires an amplitude. Therefore that stub cannot verify current recovery behavior as written. Do not count it as passed integration coverage.

## Reproduced and fixed in this audit

- Duplicate detection discarded reversed comparisons and changes involving negation. Regression tests exercise the shipping C# rule. It now respects word order, negation and interrogative changes.
- Interview system-audio questions could be rejected for sharing vocabulary with the displayed answer. Answer-readback filtering now applies only when microphone audio is included.
- Short native-rate PCM packets were stretched to the requested output length, changing duration and pitch. Generated 1 kHz audio tests cover 24/44.1/48/96 kHz and 1/2/8 channels. The shipping conversion now follows physical sample rates and pads missing time with silence.
- Empty 8 kHz PCM conversion raised IndexError. Empty-input tests now cover 8/16/48 kHz.
- Quiet but healthy microphones no longer start blocking device scans. Recovery now requires consecutive digital-dead samples, with 2-second startup and 4-second post-success bounds.
- Isolated loud taps no longer establish watchdog evidence. Recovery requires sustained high-amplitude samples for at least one second, and stale evidence expires.
- The Windows client no longer replays billable answer POSTs without an idempotency contract. Provider retries remain inside one server request.
- A server race allowed an asynchronous credit deduction to complete after a provider exception and escape its refund path. Charging is now completed inside the protected stream lifecycle. Tests cover failed provider, rejected charge, empty provider retry and delivered answer; each asserts one deduction and the correct refund count.
- A live loopback test exposed premature Auto submission: the provider repeated the same 34-character partial while audio continued, but Auto measured only text changes and submitted before the subsequent final/boundary. Auto now treats recent provider partial events as ongoing speech. Regression tests cover identical repeated partials, provider idle fallback, final-after-partial and explicit boundaries.
- The quiet split-question test exposed a second premature-submit path: provider-inferred punctuation looked final before an extension. Auto now waits through a bounded continuation grace period regardless of `.`/`?`, preventing the observed double answer and double charge.

## Verification and limits

- C# regression suite passes, including 50 cumulative transcript turns, extensions, changed intent, empty/stalled answer streams, account isolation and resume grounding.
- Python audio tests execute actual shipping helper functions extracted with AST; they do not import the engine or open hardware. Five test methods include a 12-configuration tone matrix plus quiet-room and dead-route recovery cases.
- The complete backend Maven suite passes, including billing/refund race regression tests.
- Built the backend JAR after those tests (`backend-0.0.1-SNAPSHOT.jar`, 129,519,561 bytes). It has not been deployed by this audit.
- Launched the corrected unpackaged Release app. The bundled engine opened saved microphone index 4, followed the current Windows default output, and reported `STATUS: ONLINE` about 2.6 seconds after connecting. The WPF process remained responsive. This verifies startup/readiness, not recognition accuracy.
- Python source contract checks pass. These are structural checks, not evidence of provider reliability.
- Live provider tests now cover a normal question and a paused linked question. No network interruption, physical headset/device-change, sleep/resume or clean-machine packaged-installer endurance test was performed.
- A live provider/loopback run did execute, but the Windows default output was an active TV playing an English movie. The path successfully transcribed, Auto submitted, the backend returned HTTP 200, and the first answer token arrived 432 ms after submission. The content/accuracy result is invalid because movie speech overlapped the synthetic question. Capture was stopped immediately after that was identified.

## Remaining release blockers

1. The old debug log records a speech-without-transcription watchdog restart at 01:59:58 after 14 seconds. Sustained-audio evidence mitigates that false-positive mechanism and current live utterances succeeded, but physical device failure/recovery still needs hardware testing.
2. Continuous capture while answering is implemented for system-only Interview mode. Mixed microphone Practice mode still pauses while an answer streams. Do not advertise lossless capture across all modes.
3. Continuations remain bounded heuristics. The real 1.4-second split test passes after the fix, but 3–4 second speaking pauses, more than two extensions and slow answer streams still need live endurance testing.
4. Verify real default-device changes, unplug/replug, reconnect/token expiry, sleep/resume and installation on a clean Windows account. The private-test package carries the corrected engine, but hardware and clean-machine checks require the actual machines.

Do not label this build risk-free. These blockers need measured evidence before public release approval.
