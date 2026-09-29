# Release checklist (Windows)

Written 2026-09-29 after a Free-plan tester saw "55 credits, 0m left", spoke to a
silent app for minutes, and decided her laptop was broken. The code and tests
were green. Nothing had ever been run as a new Free user, and the app, server and
website did not agree with each other. This list closes those gaps.

## 1. Everything agrees (run first, must print ALL IN SYNC)

    cd ..\uiii\frontend
    node scripts/check-sync.mjs

It checks that the server, website and this app agree on credits per plan, on
what one answer costs, on the hidden fair use limit, that customers see credits
only (no minutes, no hours), and that no plan can lose money in its worst case.
If you change any plan number, change it everywhere it names, and rerun.

## 2. Tests

    dotnet build InterviewCopilot.csproj -c Release
    dotnet run --project tests/CleanerTests --configuration Release
    python tests/test_engine_contract.py
    python tests/verify_output_pipeline.py
    python tests/test_recording.py
    python tests/test_store_packaging.py
    python tests/test_audio_samples.py

## 3. Every "it is not working" state is explained in words

`ListeningProblems.cs` describes each one, and `ListeningProblemTests` fails if a
state has no explanation or states a number. Adding a state means adding words.
To see them on screen in a developer build: `REPLYSIS_SHOW_NOLISTEN=1`.

## 4. Walk it as a brand new FREE user, not as the owner

The owner's account is on Max with thousands of credits, which hides every limit.
Before releasing, check as a Free user (100 credits, no history):

- First launch: the Setup page says what Interview and Practice hear, in words.
- Interview mode, speaking yourself: a banner says your voice is not picked up.
- Auto: a question asked out loud is answered; Space answers what was heard.
- Credits at 0: the app says so in words, with See plans.
- Fair use limit reached: the app says so in words, and credits are described as fine.
- Offline / server down: a message, not a bare label.

## 5. Automated Auto run (silent)

A recorded interviewer through a virtual audio cable into a developer build:
`REPLYSIS_AUTOTEST=1 REPLYSIS_AUTOTEST_SYSDEVICE=<cable index>`. Includes killing
the speech engine mid run; Auto must recover.

## 6. Mac

Every client change goes in `MAC_CATCHUP.md` in the same commit.

## 7. Then release

Owner clicks Run workflow (version + notes). Afterwards verify: installer and
inner programs signed by Varoxel LLC, launch the shipped exe, bump
`WINDOWS_DIRECT_DOWNLOAD` in the website and deploy.
