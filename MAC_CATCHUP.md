# What the Windows app learned, for the Mac app

Hand this to the Mac session. It is written to be read cold, by someone with
no memory of the Windows work.

Everything here came from one person testing in front of a real screen. None
of it was found by reading code, which is why the reasoning matters more than
the diffs: the same mistakes are waiting in any client that talks to the same
backend.

---

## Tell people to turn the microphone off in a real interview (after 1.0.18)

Added 2026-09-15, Windows committed, not yet released. Copy it on Mac.

**Why.** One mode opens computer sound and the microphone together, and Auto
cannot tell who spoke. Read-back detection catches a candidate reading the
answer on screen, and the question filter ignores "okay", "yes" and speech of
several sentences. What still gets through is the candidate's OWN answer spoken
as one sentence of five or more words: Auto can take it as a new question and
replace the answer. In a real Zoom, Teams or Meet interview the interviewer
arrives through computer sound, so turning the microphone off removes that case
entirely. People only do that if they are told, so the app now tells them.

**What changed, with the exact words used on Windows:**

- Settings, LISTENING info card, second sentence added: "In a real Zoom, Teams
  or Meet interview, turn off Use my microphone below, so only the interviewer
  is heard."
- Settings, "Use my microphone" switch description is now: "Turn off for real
  interviews, so your own voice is never taken as a question. Keep on to
  practice alone."
- Toolbar: the first time AUTO is chosen in a launch with the microphone on,
  the AUTO | MANUAL switch is replaced for four seconds by: "Real interview? Mic
  off in Settings". Once per launch.
- Website how-it-works guide carries the same advice (already live).

## Deepgram: a server that closes at once no longer loops forever (after 1.0.18)

Found by audit 2026-09-15, fixed in the shared engine, not yet released.
`run_deepgram()` reset its failure count on every successful handshake, so a
server that accepted and then closed immediately (no credit mid-session, a
policy refusal, a proxy that drops websockets) was reconnected in a tight loop
and never handed over to Speechmatics. A session shorter than
`_DEEPGRAM_HEALTHY_SECONDS` (5 s) now counts as a failed connection with
backoff, and two of them hand over. Proved against a local server that accepts
and hangs up: two sessions of 0.0 s, then Speechmatics came online. The contract
test "Deepgram instant closes count as failures" guards it.

## Shutdown opened a second paid session on the way out (older bug, fixed after 1.0.18)

Found by audit 2026-09-15 and present before Deepgram. When shutdown.flag was
written, MixedStream raised to end the session, the Speechmatics endpoint loop
caught that as a failed endpoint, opened a fresh session on the next region,
then slept the reconnect delay before noticing the flag. Timed with the flag
written at a known moment: the pre-Deepgram engine (e02d14d) took 24.6 s to
exit, which is longer than the app's 6 s graceful wait, so the app killed it
and the session was stranded. The current engine took 4.6 s and still opened
the extra US session. The except block now checks the flag first and returns:
measured 0.4 s to exit, no second session. The Mac engine has the same loop, so
this applies to Mac as soon as it takes the shared engine.

---

## The header is two rows: title bar, then a control strip (2026-09-20)

The single header row needed about 1,400px of controls inside a 980px window, so
"Read screen" sat on top of "Practice" (owner's screenshot). Splitting the row is
what makes it hold at any width, rather than dropping a control people need.

- Row 1, 46px: logo, session timer, account avatar, pin, minimize, close.
- Row 2, 62px: mic + status, [Auto | Manual : Interview | Practice] on the left,
  Read screen and Compact on the right. Body moved to Grid.Row 2.
- Words that repeat an icon are gone: the "Replysis AI" text (tooltip + window
  title keep it), the F8 chip on Read screen (its tooltip lists F7/F8/F9), the
  "Compact" label, and the account name beside the avatar (still in the menu).
- Measured at the 980 default: the strip's left group ends well before the right
  group starts, so nothing overlaps.
- Mac: same two-row split if the Mac toolbar is crowded.

---

## Interview / Practice: the audio source is a toolbar switch (2026-09-20)

Owner: "everyone attends interviews in meetings, this is the main part". The
microphone choice was a Settings checkbox, people left it on in real interviews,
and the app then heard the candidate's own answers and took them as questions.

- One toolbar control now holds both choices, separated by a hairline:
  [Auto | Manual] : [Interview | Practice].
  - Interview: meeting audio only (engine --mode system). Your voice is never heard.
  - Practice: meeting audio + microphone (--mode both), for practising alone.
- Named for the situation, not the hardware. Tooltip on the group says what is
  being heard ("Hearing the meeting only" / "... and your microphone").
- Same preference as the Settings switch (AppConfig.MicCaptureEnabled), so they
  cannot disagree; Settings text now names Interview / Practice too.
- **Default for new installs is Interview** (MicCaptureEnabled now false).
  Existing users keep their saved choice.
- Switching restarts the engine (~1s). Asked for mid-question, it waits and lands
  when the question is done (ApplyPendingAudioSourceChange).
- Two tips, each shown at most once per run, each one click to act on (the
  in-app alert can now carry an action button):
  - Practice + a meeting app running (Zoom, Teams, Webex...) -> "In a real
    interview?" with "Switch to Interview".
  - Interview + listening + no meeting app + nothing heard for 3 minutes ->
    "Practising on your own?" with "Switch to Practice". This is the Google Meet
    case too, since a browser tab cannot be detected.
- Rules in AudioSourceRules.cs, tested in suite 18 (AudioSourceTests).
- Mac: same control, same names, same default.

---

## Visual refresh of buttons and glass, version 1.0.21 (2026-09-18)

- Made with ChatGPT, reviewed before commit. Visual only: shared button styles in
  App.xaml (PremiumPrimaryButton, PremiumSecondaryButton, glass surfaces),
  Glass.ApplyButtonMaterials so button fills follow the opacity slider while text
  stays crisp, green accents replaced with neutral silver, "READ SCREEN" now
  "Read screen" with a vector icon, AUTO/MANUAL selection uses the shared glass
  surface. The Read screen and compact pills became real Buttons (Click with
  RoutedEventArgs instead of MouseLeftButtonDown).
- Not touched: screen capture timing, stealth, the pin, turn-taking, prompts.
- Version bumped to 1.0.21 in InterviewCopilot.csproj and Package.appxmanifest.
- Mac: optional; adopt the neutral palette if it should match.

---

## Transcription must survive a long interview, and Auto must keep turns apart (2026-09-17)

Found by running a 12-question mock interview through the real app, and by the
owner's own session log.

- CRITICAL: the temporary transcription token lasts an hour, and the engine holds
  it for the life of the process. After an hour a real session lost Deepgram with
  401, fell back to Speechmatics with the same stale key, was refused, printed
  FATAL and stopped for good: silence for the rest of the interview, with the
  question box empty and "firing AI (0 chars)" on every Space.
  - The app now renews on the engine's own words (Refused with 401, API key
    rejected, not_authorised, Not Authorized), not only on an exit code;
  - renewal may happen as often as needed, not once per run;
  - and a timer renews the token when under 8 minutes remain, while idle.
  - Mac: same failure applies wherever a temporary key is passed once at start.
- Auto mode turn-taking (AutoTurnRules.cs, tested in suite 17):
  - a continuation must start within 4s of the last submission, so the next
    question's opening words are not glued onto the previous question;
  - spelled-out noise ("Capital m o t o g p f") is never a continuation;
  - a corrected or re-delivered copy of the question just answered is not a new
    question (recognition drops words: "do you have questions for me" came back
    as "do you have for me" and was answered again);
  - the question remembered is the one actually sent, not the shorter text the
    decision was made on;
  - a transcript that still opens with the answered question has it stripped, so
    the interviewer's reply is judged on its own;
  - "Can you tell me" and other bare request openings wait for the rest;
  - "reading our answer back" now compares meaningful words only: it was
    ignoring real questions that shared "you", "the", "to" and "work".

---

## The candidate stops asking questions after the first one (2026-09-17)

- Owner: when the interviewer keeps saying "anything else?", the app kept asking
  new questions, which gives the copilot away.
- The invitation detector was an exact-phrase list: of 20 ordinary wordings it
  caught 7. "Any more questions?", "Any final questions?", "Do you want to ask
  anything else?", "Anything else?" all reached the model, which asked again.
- Now InvitationPattern (regex) catches the general forms, excluding "any
  questions on the approach before you start coding?"; and once the candidate has
  been invited, short follow-ups ("Anything else?", "Did that help?", "Was that
  clear?") also count, but never before (mid-interview "Anything else?" asks for
  more on the last answer).
- The first invitation still asks ONE question; every later one gets a local reply
  with no question, varied so no sentence repeats in a row, and "Did that help?"
  gets "Yes, that was really helpful..." instead.
- Tested: 20/20 wordings handled, 0 of 36 normal questions (with and without the
  invitation earlier) wrongly closed. Suite 16.

---

## Fixes from replaying two real interviews (sessions 411 and 413, 2026-09-16)

Both sessions were replayed turn by turn through PromptBuilder with the real
history, and the problem turns re-run against the live model.

- "Can you tell me / describe / walk me through / please describe ..." was YesNo
  and got 1-2 sentences: 14 of 31 turns in one interview. A polite request prefix
  is now stripped and the rest classified; "your (past) experience/background/
  resume" is Intro. Suite 16, InterviewTurnTests.
- "How do you see a role like this fitting into that path?" matched the screen
  phrase "do you see" and went to the screen reader, which replied with template
  text ("SAY THIS ... [your previous company]"). "do you see"/"can you see" now
  count only when they point at something ("this code", "my screen"), never at a
  role, "yourself" or a future. "How/where do you see" is General.
- Visa and work status ("what is cap extension?", H-1B, OPT, EAD, sponsorship)
  is YesNo with a reminder to state only what the facts give, never explain rules
  or timelines, never say "profile" aloud. The old example invented "no
  sponsorship needed for the next two years".
- "What are your strengths / why should we hire you" have their own format (two
  strengths with proof from the facts) instead of "why this company".
- Collaboration with researchers/cross-functional teams is Situational.
- After the candidate's questions, a long interviewer turn (45+ words, no
  closing question) is a ContextStatement, and that acknowledgement no longer
  asks another question.
- Every answer: never claim a tool, library, version or cluster size not named
  in the facts (a real answer invented Horovod, NCCL 2.14, 4-to-16 GPUs).

---

## Audit fixes on today's changes (2026-09-17)

- Engine: static needs both >5% of samples at the rail AND a zero-crossing rate
  above 0.3, so a loud voice clipping on a hot mic is never muted as static.
- Engine: after 3 searches the mic search keeps retrying every 30s instead of
  stopping; a headset unplugged during three silent searches left it deaf.
- Prompt: "What is Kafka and how have you used it?", "What is the difference
  between X and Y?" and "pros and cons" questions are no longer treated as plain
  definitions (the whole clause became the term, matched nothing in the resume,
  and the answer was told never to claim Kafka). DefinitionTerm keeps dots inside
  names, so "What is Node.js?" is Node.js, not Node.
- Ctrl+Alt+R in compact mode raises the compact window instead of showing the
  hidden main window over it.

---

## Pin in front, Ctrl+Alt+R, and a mic that dies mid-interview (2026-09-17)

- Stealth mode hides the window from the taskbar and Alt+Tab, so once another
  window covered it, or it was minimized, the owner had no way back to it.
  - New pin button left of Minimize. Pinned (default, AppConfig.KeepOnTop = true)
    keeps the main window Topmost; click to unpin; remembered.
  - Ctrl+Alt+R from any app restores and raises the window (GlobalHotkey
    OnBringToFront, BringToFrontChord). Not swallowed, so AltGr+R still types.
  - Minimize tooltip says Ctrl+Alt+R brings it back.
  - Mac: the same problem exists wherever the window hides from the Dock/Cmd+Tab;
    give it a pin and a global shortcut.
- Engine microphone, on top of the static guard below:
  - a mic that worked and then gives nothing at all for ~10s (unplugged, driver
    stopped, locked by another app, read errors) is searched for again;
  - hearing a voice resets the search count, so every loss gets 3 tries;
  - a search waits until system audio has been quiet for 2s, because it holds
    up reading and would otherwise cost the interviewer's words;
  - read errors are logged once per 50, not every chunk.

---

## Microphone search never picks static (2026-09-17, shared engine)

- On the owner's laptop every input opened through DirectSound at 16 kHz mono
  returned white noise at full scale (RMS about 19,000, 15% of samples pinned,
  zero-crossing rate 0.5), the built-in mic array included. The same devices
  through MME read a quiet room normally.
- When the chosen mic was silent, _find_a_microphone_that_hears picked the
  loudest input, which was always that static, and mixing it in drowned the
  system audio too: nothing was transcribed, mic on or off in practice.
- Now: _is_capture_noise() flags a buffer with more than 5% of samples at the
  rail; DirectSound inputs are never offered as a switch; static from the open
  mic is replaced with silence and triggers a new search; the search runs up to
  3 times (one probe can land in a pause). Tests in test_engine_contract.py.
- Mac: the Mac mic path does not use DirectSound, but take the engine for the
  static guard.

---

## "Java is", never "Java's"; off-resume terms connect to the real stack (2026-09-17, latest)

- Owner: answers must open with the name written out, "Java is", never "Java's".
  DefinitionReminder now requires that (with A/An/The when English needs it), and
  the voice rules make it the one exception to "contractions throughout".
- Owner's loaded resume was an ML resume with no Java, so "What is Java?" rightly
  claimed nothing, but read as a textbook. For a term NOT in the resume the answer
  now explains it properly, then ends with one honest sentence connecting it to
  what the resume does show ("Most of my own work is in Python..."). Tested on an
  ML, a Java backend and a frontend resume: it used Python, Java and JavaScript
  respectively and never claimed the term. No resume: no connection sentence.
- Tests added to suite 15.

---

## Answers with real substance, across question types (2026-09-17, later)

- Owner tested the previous change: "What is Java?" came back as two thin sentences
  with filler ("basically", "pretty smooth"). Their approved target: what it is, how
  it works, why it matters, about 30 seconds, spoken, tied to their own work.
- Definition answers: 4-5 sentences with the real mechanisms, still picked by
  whether the term is in the resume (see the entry below).
- Technical: 30-45 seconds with specific mechanisms and trade-offs; one clause of
  own work only if the topic is named in the resume; never claim a tool that is not.
- WhyRole: never invent facts about the company when no company or role was given
  (a test produced "you've invested in Kubernetes" with nothing to go on).
- Situational: no more "P1: a real past situation", which made the model invent a
  whole incident. Approach step by step; an example only from the resume.
- DetectType: "How do you handle a disagreement/pressure/feedback..." is Situational,
  not Technical.
- System prompt: length now matches the question (quick ones 1-2 sentences,
  technical 30-45s, stories 45-60s). Voice rules: the approved Java answer is the
  example, and filler words are banned instead of invited.
- Measured on 12 question types with the real prompts and the live model.

---

## "What is X?" answers start from the candidate, checked against the resume (2026-09-17)

- Owner tested 1.0.20: "What is Java?" answered "Java lets you write code that runs
  on any platform with a JVM", with GC tuning and Java 17 records under MORE TO SAY.
  Read as a textbook. Banning "X is a" had only moved the definition into "X lets
  you": 10 of 12 live answers opened that way.
- PromptBuilder now picks the definition format line in code. DefinitionTerm()
  pulls the term out of the question; FactsMention() checks every meaningful word
  of it against the resume (short words like Go need a capital); DefinitionReminder()
  returns one of two lines naming the term:
  - in the resume: the first sentence says where it sits in your work;
  - not in the resume, or no resume: never say you use it, open with why it matters.
- Why in code: told to check the resume itself, the model still said "Rust's the
  language I use" and "Terraform is the tool I use" for a candidate with neither.
- Live model, real prompts: on-resume terms 6 of 6 open from the candidate's work,
  off-resume terms 0 of 9 claim use. MORE TO SAY asks for a gotcha, trade-off or
  alternative, never spec facts or version features.
- Tests: suite 15, DefinitionVoiceTests. Mac: port the three methods and pass the
  resume into your format reminder the same way.

---

## Recording saved marker written only when it is true (2026-09-17)

Shared engine fix, found by a second external review.

- The app waits for recording_saved_<id>.flag when a session ends, then encrypts
  the WAV. The audio loop wrote that marker on every 100 ms cycle whenever no
  recording was running: about 36,000 file writes an hour. Worse, the cycle after
  a recording stopped wrote it while the background save was still writing the
  file, so the app could encrypt half a recording and leave the raw WAV behind.
- Now the loop calls mark_nothing_recorded(), which writes the marker once, and
  never for a recording this engine started. Only save_recording() marks a real
  recording saved, after closing the file. A session that ends before any audio
  was recorded still gets its marker, so the app does not sit out its timeout.
- Windows waits for the marker only when the session was saving audio
  (isRecording && _savingSessionAudio), on New Session and on exit.
- test_engine_contract.py checks all of this. Mac: take the shared engine, and
  gate your own wait the same way if you have one.

---

## Recordings off by default, screen keys switchable, F12 moved (2026-09-17)

Owner's decisions after an external release review. Continuous screen capture
is intentional and was NOT changed: every 2s while listening, every 15s for 5
minutes after mute, Read Screen and F8/F9 as before.

- Session audio recordings: every session used to write record.flag, so the
  engine recorded mixed audio for up to 90 minutes, kept it encrypted for 7
  days, and nothing ever played it back (the Sessions panel only deletes it).
  Now off by default behind a Settings switch, "Save session audio"
  (AppConfig.SaveSessionAudio, default false; old config files load false). When
  off, StartNewSessionAsync removes any stale record.flag. The status label says
  LISTENING instead of RECORDING unless audio is actually being saved.
- F7, F8, F9: plain keys still read the screen from any app by default. A new
  switch, "Screen keys work in every app" (ScreenKeysEverywhere, default true),
  hands those keys back to other apps when off (an IDE uses them for debugging),
  and Ctrl+Alt+F7, F8 or F9 always work.
- F12 opened a support debug window and swallowed F12 from every app, which is
  browser DevTools and an IDE's go-to-definition. It is now Ctrl+Alt+F12 only
  outside Replysis, and every "Press F12 for details" message says Ctrl+Alt+F12.
- Ctrl+Alt chords can arrive as WM_SYSKEYDOWN, so the hook promotes those to a
  normal press only when Ctrl is held too; Alt+Space and Alt+Tab stay untouched.
- Tests in suite 14 cover the key rules and all three defaults.

---

## Update check honesty, overlay height, CI runs the tests (2026-09-17)

- Settings "check for updates" said "You are up to date" when the check FAILED:
  a network error, a timeout and "no newer version" all returned the same null.
  UpdateService.CheckForUpdateAsync now returns an outcome (Staged, UpToDate,
  NotInstalled, Failed) and Settings shows the failure message on Failed. The
  quiet launch check still uses CheckAndStageAsync and behaves as before.
- Compact overlay transcript: 1.0.20 removed the three-line cap, but with no cap
  a long turn grew the overlay past the bottom of the screen. It now caps at
  eight lines (152px), scrolls beyond that with the scrollbar hidden, and keeps
  the newest words in view.
- CI (verify-windows.yml) only compiled. It now runs CleanerTests, the engine
  contract and the output pipeline tests on every push.

---

## Closing turns, second pass (after an external review, 2026-09-17)

An outside review ran exact sentences through the 1.0.20 classifier. Every one
of its findings reproduced on the build, and all are fixed:

- "We'll be in touch with next steps, but first can you explain your testing
  approach?" returned a goodbye. Strong closing phrases now count only when no
  question or task follows them.
- "Does that answer your question so how would you test this service" (no
  punctuation, as speech recognition often delivers) got a fixed wrap-up. The
  last-sentence split needed punctuation. Now: find where the last invitation
  phrase ENDS and check whether a question word or task follows it.
- "Is there another angle on the role, the tech, or the team that you'd like me
  to focus on?" was not recognised. It is, when it names the role, team or
  company and asks what to focus on; "what other angle would you take to reduce
  latency" still goes to the model.
- A final turn that recaps earlier questions and then thanks the candidate was
  not a sign-off, because the whole turn was scanned for "?" and "question".
  Sign-offs are now judged from the LAST thank-you onward.
- Coding tasks phrased as a task rather than a command ("For this next exercise
  I want a function that...", "The next exercise is a SQL query returning...")
  were only acknowledged. Coding detection now runs first, IsCodingRequest
  recognises task phrasing, and the acknowledge-only rule needs positive
  evidence of an explanation (we, our, the team, the role).
- "Can you think of a specific project where..." was YesNo and got a yes/no
  style answer. Story cues now win over the yes/no opener.
- The system prompt now forbids stating immigration or legal facts (what STEM
  OPT, H-1B or an EAD allows) beyond the candidate's own profile. A real session
  claimed STEM OPT works "without needing an EAD", which is wrong.

Verified against a real 17-turn session run statefully in order (locally only,
never committed; the repository is public): turns 1-11 and 15 go to the model
with sensible types, the first question invitation goes to the model, repeat
invitations and the final thank-you get the short local replies. Tests: suite
12 now has 40 cases, all passing.

---

## 1.0.20: system audio that stays on the call, and closing turns

Windows, 2026-09-17. Both halves apply to Mac: the engine change is in the
shared speechmatics_engine.py, and the prompt change is in PromptBuilder.

### System audio went deaf in a real Google Meet

Owner's own call, microphone off so only computer sound was heard. The log:

    19:37:56  SYSTEM AUDIO (WASAPI loopback): Speakers (2- Realtek(R) Audio)
    19:37:58  FINAL received          <- the one question that worked
    19:38:23  HOTSWAP CABLE In 16ch (VB-Audio Virtual Cable)
    ...then CABLE Input, NVIDIA Broadcast, Sonar Gaming, all silent

Meet played into the Realtek speakers the whole time. After ~3.5s of quiet the
engine assumed it had the wrong device and rotated to virtual cables nothing
plays into. Result: one question in Auto, then nothing; nothing at all in
Manual. One bug, both symptoms.

Pinning to the Windows default is not the fix either: on this machine the
default output IS a VB-Cable. So the rule is now "follow the sound":

- Quiet is never treated as a wrong device.
- After the silence threshold, `_follow_the_audio()` probes a few other
  loopbacks (4 per sweep, 200ms each, cursor covers the rest over time) and
  moves ONLY to one that answers and carries real signal.
- If nothing else has sound either, it stays and says so once.
- The old blind rotation is kept only for devices that hang.

Proven: two runs with 30s of silence showed 0 hotswaps where the old engine
made 4, and a spoken passage transcribed cleanly. NOT yet proven: the case
where sound plays on a different device than the one selected, because the
test voice played through the same device. Watch for ">>> SYS AUDIO found sound
on" in a real call before relying on it.

### Closing turns, and three rules that answered real questions locally

A second model added handling so "any other questions?" loops end and a final
thank-you gets a short goodbye. Good idea, but three of its rules answered
ordinary interview questions with a fixed line and no model call. Each was
confirmed against the shipping code before fixing:

- "Thank you for taking the time to speak with us today. Can you start by
  telling me about yourself?" was answered with a goodbye. Almost every
  interview opens that way. A thank-you now counts as a sign-off only when it
  has no question mark and does not carry on ("let's", "can you", "start",
  "next", "background"...).
- "What other angle would you take to reduce the latency here?" was answered
  "That answered what I wanted to know." Ambiguous phrases were removed, and
  only the LAST sentence of a turn is checked, so "Does that answer your
  question? So how would you test this?" goes to the model.
- A rule treated any 18+ word turn without a question mark as an explanation
  to acknowledge. "So for this next one I want you to describe how you would
  design a URL shortener" was acknowledged, not answered. It now also requires
  the turn not to address the candidate (you, your, walk me, imagine, design...).

Tests: suite 12 has 25 cases, including every sentence above. QuestionType and
DetectType are now internal so the rule can be tested directly.

### Also in 1.0.20

- Settings "check for updates" was broken for everyone: releases are tagged
  v1.0.19 but the parser only accepted windows-v1.0.x. Both forms now parse
  (suite 13).
- Answers are shorter: most 2-5 sentences, 25-40s aloud; MORE TO SAY is 2-3
  points, not 4-6; at most two tools named unless asked.
- A live tip card at the top of Settings: amber "turn your microphone off"
  when the mic is on, green "ready for a real interview" when it is off.
- Compact overlay: the interviewer transcript no longer clips at three lines.
- Version 1.0.20.0 in the csproj and the Store manifest.
- The engine log said "while Speechmatics connects" even on Deepgram. Fixed.

### Space typed in another app no longer toggles listening

The Space hotkey is system wide on purpose (the meeting window has focus in an
interview), but it toggled on every Space and only ignored typing inside
Replysis's own text boxes. The owner's log, while typing a chat message in
another window, showed MUTED / UNMUTED every one to two seconds: each space
between words flipped the microphone.

Now a Space that follows another character key (letters, digits, punctuation,
Backspace) within 1000ms is treated as typing and passed through untouched.
Ctrl, Shift or Win plus Space never toggles, since those are system shortcuts.
A deliberate toggle only needs a one second pause after typing. Ignored presses
log at most once every five seconds. The decision is a pure function
(GlobalHotkey.IsSpaceAToggle) with its own test suite, 14. The Mac hotkey
almost certainly needs the same rule.

---

## Real questions were answered with "Doing really well, thanks!" (fixed after 1.0.19)

Reported by the owner 2026-09-16, fixed on Windows, NOT released yet. The Mac
has the same function in PromptBuilder.swift and almost certainly the same bug.

**Symptom.** Different questions kept getting the identical chit-chat answer,
including real ones. The canned line is returned locally, before any model call,
so nothing in the logs says an answer was invented and the candidate is not
warned: the panel hears "Doing really well, thanks! Excited to be here and learn
more about the role" in reply to a technical question.

**Cause.** `IsSmallTalk` fired when a line under sixty characters merely
CONTAINED a pleasantry ("how are you", "nice to meet"), unless it also contained
one of about twenty hardcoded words: what, why, explain, java, python, sql,
code, project, experience and so on. Anything outside that list was treated as
chit-chat. Real questions that lost:

- "How are you handling state in React?"
- "How are you deploying to AWS?"
- "How are you testing this?"
- "Nice to meet you, shall we start with your background?"

None contain a listed word. All are short. All got the canned reply. The rule
has been there since the first commit, so it has been shipping the whole time.

**Fix: subtract instead of allow-listing.** Take the pleasantry away, take the
filler away (hi, thanks, today, sir, you, doing, and so on), and if any word is
still standing the interviewer asked something, whatever it was about. A list
can only ever name the technologies somebody thought of; subtracting asks the
question the code actually cares about. The phrase list is now one array used
both to match and to subtract, so the two cannot drift apart.

Unchanged on purpose: the sixty character early-out, and `IsGreeting`, which is
tight already (exact "hi"/"hello"/"good morning", plus repeats like "hello
hello" from recogniser artifacts).

**Tests.** New suite 11 in tests/CleanerTests (SmallTalkTests.cs), 21 cases,
calling the shipping PromptBuilder directly rather than a copy: the five real
questions above must reach the model, and plain pleasantries must still skip it.
All pass. Worth porting the cases to the Mac verbatim.

---

## Deepgram is now the first recogniser for English, Speechmatics the fallback

Added 2026-09-15. Windows only so far; the Mac is unaffected until it opts in.

**Why.** The owner asked for transcription faster than Speechmatics. Measured by
streaming the same audio live to every provider at once, first on a synthetic
64-second interview answer and then on the owner's own voice:

| | words on screen after spoken | errors on the owner's voice |
|---|---|---|
| Deepgram nova-3 + keyterms | 0.14-0.17 s | one inserted word |
| ElevenLabs scribe_v2_realtime | 0.34-0.50 s | none, but a partial only about once a second |
| Speechmatics enhanced (before) | 0.35-0.37 s | "and are" for a filler, "Redies as a cash" on the synthetic clip |

Without keyterms nova-3 was no better than Speechmatics ("Kubernets", "Readys",
"Next dot j s"), so keyterms are the reason this works, not a tuning extra.
Deepgram also allows 150 sessions at once on pay-as-you-go against the 50 that
capped Speechmatics. Soniox could not be measured: its account has no balance.

**Backend (live for both apps).** `GET /api/v1/stt/key` now also returns
`deepgramToken` when `DEEPGRAM_API_KEY` is set on the server. It is minted with
Deepgram's `/v1/auth/grant` for the same 3600 s as the Speechmatics token and
behind the same identity, credit, listening-time and rate checks. It is never
required: without the key, or with Deepgram refusing, the response is exactly
what it was. Unsetting that variable turns Deepgram off for every install.

**Engine (`speechmatics_engine.py`, shared).**

- `DG_TOKEN` in the environment plus `--language en` runs `run_deepgram()` first.
  Anything else runs exactly as before. No token means no change, which is why
  the Mac is untouched today.
- It returns only to exit or to hand over. HTTP 401, 402, 403 or 429 on the
  handshake hands over at once; two failed connections in a row hand over too.
  main() then carries on into the Speechmatics path in the same process, relay
  included, so the worst case is the old latency, never silence.
- Same stdout contract: `STATUS: ONLINE`, `STATUS: OFFLINE`, `PARTIAL received`,
  `FINAL received`, and `UTTERANCE END` from Deepgram's UtteranceEnd event
  (`utterance_end_ms` from `--utterance-silence`, floored at Deepgram's 1000 ms).
  Same latest.txt, pause.flag and reset.flag behaviour.
- While muted it sends Deepgram `KeepAlive` instead of silence. Deepgram bills
  audio, not connection time, so a muted session costs nothing.
- `MixedStream` and `BufferedMixedStream` moved unchanged from inside main() to
  module level so both recognisers share one capture path. `BufferedMixedStream`
  gained `read_timeout()`, which only the Deepgram sender uses.
- Keyterms: the built-in tech and job terms, then vocab.txt, capped at 100.

**For the Mac to opt in:** read `deepgramToken` from the `/stt/key` response,
cache it with the Speechmatics token, clear it wherever that token is cleared,
and pass it to the engine as `DG_TOKEN`. Nothing else changes. Windows does this
in `UserSession.cs` (`DeepgramToken`, carried through `sttkey.json`) and
`MainWindow.xaml.cs` (`DG_TOKEN` next to `SM_API_KEY`).

---

## The backend is shared, so half of this is already yours

The Mac app talks to the same server. These are live and need nothing from
the Mac side:

**Answers no longer run on Groq at all.** Corrected 2026-09-11; the paragraph
that stood here described the Groq chain and had been false since `164ccd7`.
Every answer path is now `gemini-3.5-flash-lite`, with `gemini-3.1-flash-lite`
behind it. Both verified answering 200 on the live key.

Groq was faster and still is, by roughly 200 ms. It was dropped anyway, because
Groq sells no capacity above 8,000 tokens a minute - about three and a half
questions a minute for the whole product, across every user. Speed you cannot
buy more of is not speed you can build on. Falling back onto a hard-capped free
tier is not a fallback either: at any real volume it is already exhausted by the
time it is reached, so the fallback is a second Gemini model rather than Groq.

The Groq branch still exists in the source and is reachable only when the Gemini
key is absent. With the key present it is dead code. If the Mac app pins a
provider string, note that the backend ignores it for the answer path now.

**Screen analysis runs on Gemini**, `gemini-3.5-flash-lite`, same key as the
answer path. It ran on Groq `qwen/qwen3.6-27b` when the paragraph below was
written, and the reasoning in it is still worth reading even though the model
named has changed.
The code used to say no Groq vision model existed. It was wrong: asking each
model for an image is how you find out, and qwen answers "image must have at
least 2 pixels", which is a complaint about the test pixel and means it read
the image.

**Coding answers are written by a different model than the one that reads the
screen.** The vision model reports what is there — statement, language,
existing code, the error and its line — and a second model writes the answer
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

## The window work, which this file should have carried and did not

Written late, and the omission is the point. Every entry above is about the
answer pipeline, the engine, or billing, because those are what the two
sessions were talking about. A fortnight of interface work landed on Windows
and never reached this document at all — no mention of Past Sessions, the
resume filename, the debug window, or the compact bar. The owner noticed, not
either session.

The rule in this repo is that a Windows change goes in here in the same commit.
It held for everything that came up in conversation and failed for everything
that did not, which is the more dangerous half: nobody asks about the part
nobody is discussing.

**Past Sessions is a panel, not a window.** `SessionsWindow` is deleted.
`SessionsPanel` is a `UserControl` hosted at `Grid.Row="1"` inside
`NormalModeGrid` (`MainWindow.xaml:1237`), and the interview content collapses
behind it. It opened as a separate 1040×640 window — larger than the 980×600
main window, with no opacity of its own, so it broke the illusion of one
surface and was plainly a second application. Esc closes it, handled in
`Window_PreviewKeyDown` only while the panel is visible. The close control says
**"Back to interview"** rather than ✕, because ✕ on a panel inside a window
reads as "quit", and someone mid-interview should not have to wonder.

**One opacity for every window.** `Glass.cs` is the single source:
`Glass.BackdropFor(storedOpacity)` derives a backdrop brush, `Glass.Apply` sets
it on a window's root `Border`, and `Glass.ApplyToOpenWindows` re-applies to
everything open when the setting changes. Each window used to decide its own,
so changing the main window's opacity left the others opaque and obviously
separate. **No window may be larger than the main window** — that was a
specific instruction and it is not merely aesthetic: a dialog larger than the
app it belongs to is the clearest possible tell that something extra is
running.

**The resume shows its own filename.** `LoadSavedResume(content, name)` carries
the name through rather than reconstructing a label; the list previously showed
"Resume · <date>" for every entry, so three resumes were indistinguishable.
Files are read with `FileShare.ReadWrite | FileShare.Delete`, because uploading
a resume that was open in Word failed with a file-lock error the user could do
nothing about.

**The debug window was a stealth leak.** It showed live transcripts, was
`Topmost`, and was not excluded from capture — so on a shared screen it was the
one window that gave everything away. It now calls
`WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode())` on load
(`DebugWindow.cs:51`) and follows the same rule as everything else. **Stealth
means every window, with no exceptions** — a single visible window is the same
failure as none of them being hidden.

**The compact bar reads the screen.** Its button was still the old "Watching"
toggle after that moved to Settings. It now calls `HandleScreenAnalysisAsync()`
— the same path as F8 — so the compact bar can do the thing the full bar can.

Mac parity here is **unassessed**. None of this was ever reported, so the Mac
session has had no opportunity to say whether it applies, already exists, or is
irrelevant on that platform. Treat it as a list to triage rather than a list of
gaps.

## The screen upload limit is nginx's default, not our policy

Mac's screen answers were returning 413. The cap that fires is **not** the
2 MB per-image rule this document described, and not anything in the
application:

    application.properties  multipart 10MB      not this (not multipart)
    JacksonSecurityConfig   maxStringLength 2M  not this (chars, and 2M)
    MAX_STASHED_IMAGE_CHARS 2_000_000           never consulted
    nginx client_max_body_size    UNSET  ->  DEFAULT 1 MB   <- this one

`client_max_body_size` appears nowhere under `/etc/nginx/`, so nginx applies
its 1 MB default and refuses the request before any application limit is
reached. **The 2 MB figure describes a limit that cannot be met.** A 820 KB
PNG becomes ~1.09 MB as base64 and is rejected at the proxy.

**Windows is PNG too, not JPEG** — `PngBitmapEncoder`, deliberately, because
JPEG rings around glyph edges and that is the difference between a model
reading `l` and reading `1`. So the guess that Windows was safe by encoding
format is wrong. Windows is safe because ordinary screens compress well: a real
capture measures 239 KB, about 319 KB encoded.

**But nothing capped the size.** The caps were on dimensions
(`MaxShortEdgeFullScreen` 1100, `MaxLongEdgeFullScreen` 2560) and the palette
reduction is best-effort. A photograph, a gradient, or a video call defeats the
palette entirely, and at that resolution could clear 1 MB encoded. Windows had
never hit it by luck of typical content, exactly as the Mac session suspected.

`WithinUploadBudget` now enforces **700 KB raw / ~930 KB encoded**, the same
figure Mac chose. Order matters and follows §8: squeeze the palette first
(128 colours, then 64), because a code editor has no shades to lose and every
glyph edge stays sharp — and only scale down as a last resort, logging when it
happens, because shrinking is what drops body text below what the model reads,
which is the failure the larger capture existed to fix.

### RESOLVED 2026-08-25: `client_max_body_size 3m`

Set in the `http` block of `/etc/nginx/nginx.conf`, validated, reloaded with no
downtime. Backup at `nginx.conf.bak-bodysize`.

**3m rather than 2m on purpose.** A 2 MB base64 image plus its JSON wrapper is
about 2.1 MB on the wire, so 2m would have re-created the same
unreachable-limit problem one layer up — the documented figure again just
barely unmeetable.

Measured against production afterwards, rather than assumed from a successful
reload:

    900 KB  ->  401   reached the application
    1.5 MB  ->  401   reached the application (was 413 before)
    2.5 MB  ->  400   the app's own 2M-char rule, now the limit that fires
    3.5 MB  ->  413   nginx, as designed

The 2.5 MB row is the one that matters: `MAX_STASHED_IMAGE_CHARS` is finally
the effective ceiling, so the documented policy describes what actually
happens.

**Both clients keep their 700 KB budgets.** The headroom is for large monitors
and awkward screens, not an invitation to send more — a picture still costs
upload time on the user's connection, which is most of the wait.

*Worth recording how nearly this went wrong: the first attempt inserted the
directive with `sed`, which collapsed the comment block and the directive onto
one line, leaving `client_max_body_size` inside a comment. `nginx -t` passed —
because a comment is valid syntax. A successful config test would have been
taken as proof, and the setting was inert. It is the probe again: a check whose
output was independent of the thing it was meant to confirm. Only the
end-to-end request sizes prove this.*

## Which prompt wins: it depends on the path, and the two differ

The Mac session marked this unverified because it cannot see the server. It is
answerable from the backend, and the answer is that **there is no single
winner — the spoken path and the screen path are governed by different
prompts.**

**Spoken questions (`/ask`) — the client wins outright.**
`InterviewController.java:196`:

```java
aiMessages = clientMessages;   // full context from the C# PromptBuilder
```

`buildSystemPrompt` is a **fallback**, used only when the client sends no
messages at all. Both apps always send messages, so the backend's spoken system
prompt never reaches the model in practice. Whatever the client says, goes.

**Screen questions — the server wins.** Stage two is `codingMessages()`
(`:1717`), called at `:767`, and its system prompt is built server-side. The
client's `payload["prompt"]` (`:597`) is the *vision* stage's instruction and is
passed in only as context via `actuallyAsked()`. The fence requirement at
`:1732` lives in that server-built prompt.

### What follows, and it is not what either session assumed

| | spoken answer | screen answer |
|---|---|---|
| prompt owner | client | **server** |
| fences requested | whatever the client says | **always** |

- **Mac's screen answers have been receiving fences all along.** The server
  demands them and the client cannot opt out. `NetworkClient.swift:645` was
  stripping fences that were arriving correctly — this is now confirmed rather
  than suspected.
- **Mac's "no backtick fences" rule 4 applies only to spoken answers**, where
  the client prompt does win. Header-driven rendering is correct *there*.
- **So the contradiction is not a contradiction.** The two rules govern
  different paths and neither is being ignored. The Mac renderer accepting both
  is not a hedge against uncertainty — it is the correct design, because the
  two paths genuinely produce different output.
- **Windows has the mirror exposure.** Its fence-driven renderer is safe on the
  screen path because the server guarantees fences, and depends on
  `PromptBuilder`'s own instruction on the spoken path. Those are two different
  guarantees of two different strengths, and the code treats them as one.

**Before either side changes fence handling, note which path is being changed.**
A rule that is safe on one is unenforced on the other.

## The engine reports end of utterance

**Mac auto mode decides a turn is over from `>>> UTTERANCE END` and nothing
else.** The fork emitted it from a Speechmatics `EndOfUtterance` handler with
`end_of_utterance_silence_trigger` set in the config. The shared engine had
neither, so the event was never requested and the line never came.

The failure had no visible cause. The app listened, transcribed accurately,
grew a transcript for minutes and never answered once — a perfectly healthy app
waiting for a signal nobody had asked the server to send.

Added, and **emitted for both clients rather than only the one that needs it**:

    --utterance-silence <seconds>   default 0.8, 0 disables
    >>> UTTERANCE END               printed when the recogniser reports one

Windows classifies turn-end from the transcript text and currently ignores the
line. That is not a reason to make this Mac-only: the recogniser holds the
waveform and the text classifier does not, so this is strictly better
information than what Windows uses today. Making it conditional would be
deciding by omission that Windows must never have it.

The config parameter is **probed, not assumed**. `TranscriptionConfig` is a
dataclass, so an unknown keyword is a `TypeError` at construction — passing it
blind would take *both* clients down on any speechmatics build predating the
parameter, a worse failure than the one being fixed. If unsupported, the engine
says so on stdout and continues without it.

### The pattern this is the third instance of

`--sysfifo`, then the single-dash argument convention, now the turn-end signal.
Each was a Mac requirement the shared engine lacked; each was found by running
it, never by review; none was visible from the Windows side, because Windows
has no FIFO, no single-dash convention and no acoustic turn detection.

The Mac session named it exactly right: *"one engine for both apps" keeps
resolving to "the Windows engine, plus whatever Mac discovers it is missing."*
That is not anyone's fault — it is what happens when one platform's client is
the reference and the other's requirements only become visible when they break.

**The durable fix is a contract, not more care.** Before the shared engine is
called shared again, it needs a list of the outputs each client depends on,
checkable without a human noticing an absence: Windows needs `STATUS: ONLINE`,
`MIC SIGNAL DETECTED`, `PARTIAL received`/`FINAL received`; Mac needs
`UTTERANCE END` and `--sysfifo`. A missing line should fail a check, not a
silent interview. Until that exists, expect a fourth.

The first real cross-platform comparison mismatched: `6f9746fe6237` on Mac
against `f283565ab5bd` on Windows, at the same clean commit. Not drift. The Mac
session tested the obvious alternative before reporting it, which is the only
reason it was not recorded as a fork.

`core.autocrlf` is true on Windows, so the shared engine is checked out with
CRLF here and LF there. Same blob, same commit, different bytes on disk — by
git's deliberate design. The old stamp used `Get-FileHash` over the working
tree, so **it could never have matched across platforms, and would have
reported a fork on every honest build, forever.**

That is the worst way for a check to be wrong. A dirty tree at least has a
visible cause; this looked exactly like real drift, had no explanation on the
surface, and was guaranteed to recur — so the first person to hit it explains
it away, and after that nobody trusts the instrument at all.

**Use git's blob id.** It is content-addressed and line-ending normalised, so
it is identical on both platforms, and it is the id git itself stores — which
means a match additionally proves the working tree is the committed content,
something the old method could not tell you.

    git hash-object speechmatics_engine.py   ->  9d74598b48be  (both platforms)

**The no-git fallback must produce the same value, not merely a stable one.**
A fallback that stamps something different-but-consistent silently reintroduces
the bug. `build-engine.ps1` computes the blob id directly —
`sha1("blob <length>\0" + LF-normalised content)` — and this was verified to
reproduce `git hash-object` byte for byte, not assumed to. Both platforms use
this exact form.

### VERIFIED 2026-08-25: both platforms read `9d74598b48be`

    Windows  git hash-object      9d74598b48be
    Mac      git hash-object      9d74598b48be
    Mac      no-git fallback      9d74598b48be
    frozen binary stamp           dd54215 src:9d74598b48be
    app's recorded provenance     dd54215 src:9d74598b48be

The first time this check has ever produced a meaningful answer. Item 7 is
closed on evidence rather than on both sides asserting it separately.

Note that Mac built at `dd54215` and Windows published from `028baa7`. The
comparison still holds because the blob is identical at both commits
(`git rev-parse <commit>:speechmatics_engine.py`) — and being able to show that
without a second manual check is the new property already earning its place.

**What actually changed here is worth stating plainly, in the Mac session's
words: we replaced a rule people follow with a property that holds.** The old
stamp could not distinguish a clean checkout from a half-saved file, so the
"compare only at a shared commit, never a dirty tree" rule had to be remembered
and applied by whoever was looking. Now a dirty tree produces a blob id that
matches no commit, and the mismatch *is* the signal. Nobody has to think to
check.

That is the shape to aim for whenever one of these rules gets written down. A
rule that depends on a future session remembering it is weaker than a mechanism
that fails visibly on its own.

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

**Coverage is the same on both, and an earlier note here said otherwise.**
A Mac run appeared to show the detector unable to fire in Manual mode; reading
the path afterwards showed the meter has no mode check on either platform and
starts whenever listening starts, from any source. What the run actually
showed was an app sitting in Manual that had never been armed. "Not listening
is not covered" is true, and is not a finding.

Both: all modes, bounded by turn length — a turn shorter than 15s ends before
the second tick.

Worth keeping the retraction rather than deleting the claim, because the
failure is the mirror of the one this whole exchange has been avoiding: a real
observation, then reasoning from it instead of reading the code it
implicated. Running it is necessary and it is not sufficient.

## Item 9: what a listening session costs

**Windows does not double-count on reconnect.** The Mac hypothesis was an
unpaired `startListeningMeter()` restarting the clock while the pre-drop
segment was already banked. That cannot happen here: `StartListeningMeter()`
is called from exactly one place, the tick re-stamps `_listeningSinceUtc` as
it accumulates, and both `StopListeningMeter` and `FlushListeningMeterOnExit`
clear it to `MinValue` before returning. An engine reconnect does not touch
the meter at all.

**What it does instead is bill the same wall time twice at the edges.** The
two roundings run in opposite directions and compound:

- the tick **floors** whole minutes and carries the remainder forward
- the stop path **rounds that remainder up** to a whole minute at 30s or more

Every turn therefore pays for its final partial minute as a full one, on top
of the whole minutes already reported. And a turn is not a sitting: Manual and
Auto both call `StopListeningMeter()` at the end of every exchange, so the
round-up happens once per turn.

    one 90s turn        1.5 min listened   ->   2 min billed
    ten 40s turns       6.7 min listened   ->  10 min billed   (50% over)
    one 600s turn        10 min listened   ->  10 min billed   (honest)
    thirty 25s turns   12.5 min listened   ->   0 min billed   (free)

**The error is bidirectional, and which way it goes depends on how the user
talks.** The last row is the Mac session's finding and this file previously
missed it, describing the fault as a consistent overcharge. It is not. Short
exchanges overcharge by half; very short ones charge nothing at all; one long
answer is honest. The boundary is sharp: **29.9s bills 0 minutes, 30.0s bills
1.**

That is harder to explain to a customer than a flat 50% over, and harder to
defend, because two people doing the same interview differently are charged
differently for the same minutes.

**The 30-second floor itself is deliberate** and the comment defending it is
right on its own terms — rounding every short turn down to nothing would make
an interview of brief exchanges free. But it only prevents that at 30s and
above. **Below 30s it produces exactly the outcome it was named after.** The
guard causes the thing it was written to stop.

What was never decided is what should happen when that floor is applied per
turn to a remainder that has already had its whole minutes taken out.

**There is no neutral correction.** Round down and short interviews go free.
Round to the nearest second and the floor's purpose disappears. Bill per
session rather than per turn and the model changes. Every direction is a
business decision wearing engineering clothes, which is why none of them was
taken by either session.

## DECIDED, 2026-08-25: the session is the billing unit

The owner chose per-session billing. **Mac must match this exactly.**

Turns bank their seconds and report only whole minutes, carrying the remainder
forward into the next turn. Nothing is rounded up until the sitting ends. The
30-second floor survives — a genuinely brief interview is still not free — but
it is applied **once, at session end**, instead of once per turn.

    MinutesOnTick(seconds, out carry)   floor, remainder carried  (turn end AND meter tick)
    MinutesAtSessionEnd(seconds)        < 30s -> 0, else max(1, round)  (exit flush ONLY)

Windows call sites: `StopListeningMeter` now uses `MinutesOnTick`;
`FlushListeningMeterOnExit` is the only place `MinutesAtSessionEnd` is called.

What changes for a customer:

    ten 40s turns      6.7 min listened   was 10 min   now  7 min
    thirty 25s turns  12.5 min listened   was  0 min   now 13 min
    one 600s turn      10 min listened    was 10 min   now 10 min

The property to protect, and the one worth porting a test for: **the same wall
time costs the same however it was broken up.** Six minutes billed as six,
whether it arrived as one answer, twelve 30-second turns, or a ragged mixture.
That is asserted three ways in `BillingTests`.

### The exit report cannot be a blocked async call

The Mac session found its flush computing the right number and sending
nothing: an async task started in `applicationWillTerminate` is killed with
the process before it reaches the wire. Windows had the same hole by a
different mechanism, and it was there to be found by checking rather than by
assuming the platforms differ.

`FlushListeningMeterOnExit` ran `ReportListeningMinutesAsync(...).Wait(1500)`
on the closing UI thread. That method awaits without `ConfigureAwait(false)`,
so its continuation needs the UI thread to resume — and the `.Wait()` was
holding it. Textbook WPF deadlock: the continuation could never run, the wait
always burned its full timeout, and nothing was ever confirmed.

Now `ReportListeningMinutesOnExit` does a genuinely synchronous
`HttpClient.Send` bounded at 3 seconds, touches no UI, and reads nothing from
the response — there is no window left to show it in. The async version is
unchanged and still resumes on the UI thread on purpose, because it updates
the remaining-minutes display.

### DECIDED, 2026-08-25: a force-quit loses the banked minute. Leave it.

Task Manager on Windows and `kill -9` on macOS skip the handler entirely, so a
force-quit or crash loses the remainder banked since the last whole minute — up
to 59 seconds.

**Do not "fix" this.** It was considered and declined, and the argument is the
point of this entry.

Closing it means persisting the bank to disk and sending it at next launch,
which buys at-least-once delivery. At-least-once means that when the app dies
between a successful send and the record of that success, the customer is
**billed twice for the same minute**.

The two failures are not symmetric:

- A lost minute is invisible, costs pennies, and is the company absorbing it.
- A double charge is visible to the customer, provable with a stopwatch, and
  in a contract with an audit or accuracy clause it is a finding rather than a
  rounding error.

Metered billing systems resolve this the same way everywhere: when uncertain,
under-bill. Dropping an event is a cost you absorb; duplicating one is a
dispute.

There is also a timing argument specific to this week. Billing was just made
accurate in both directions; adding a new mechanism that can overcharge, days
after removing the old one, is a bad trade.

**Revisit only if** crash rates ever get high enough that the lost minutes are
material — at which point the fix is the crash rate, not the billing.

Both platforms carry this gap deliberately and identically.

**Mac has a fourth gap to close first.** There is no exit flush on that side —
the final partial turn before the app closes is never reported at all. Under
per-turn billing that lost a fraction of a minute; under per-session billing
the exit flush is the *only* place the remainder is billed, so without it every
Mac session silently discards up to 59 seconds. Build the flush before porting
the rounding, not after.

**Release condition, not an intention:** whatever is decided must land on both
platforms in the same release. A Mac user and a Windows user billed differently
for the same interview is a support problem neither session can answer.

**Not changed.** The arithmetic moved to `ListeningBilling` unaltered so it can
be read and tested, and `tests/CleanerTests/BillingTests.cs` records the
current figures. What a customer is charged is the owner's decision, not
something to alter quietly while tidying. If the rounding changes, those tests
fail and the new numbers go in.

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

## Nine changes after 1.0.17, none of them shipped yet

Recorded 2026-09-11. These landed over the week after 1.0.17 went to the Store
and this file was not updated with them at the time, which was the rule being
broken rather than an oversight worth repeating. Store users still have 1.0.17
and have none of this.

Several came from the Mac side or were found on both at once. Where that is
true it is said, because it is the argument for keeping this file current:
neither platform found all of them.

**Reading our own answer aloud was treated as a new question.** `d391348`. The
product exists so an answer appears and the candidate says it. In practice mode
the microphone hears exactly that, the words return as a transcript, and the
app answered itself - replacing the answer being read half-way through,
charging a credit, then doing it again. Nobody reports this as missing echo
suppression; they report that the answer disappears while they read it.
Compared against the text on screen rather than against audio, since the screen
is what they can be reading, and by vocabulary overlap rather than sequence,
since someone reading aloud paraphrases, skips and adds filler. At or above 55%
overlap it is not a new question. Below eight words it does not judge at all -
"and the complexity" or "I use Docker" share words with any answer by accident,
and swallowing a real follow-up is worse than the bug.

**Plain questions were taking the screen path.** `6ae0397`. Measured against
production: spoken answer 0.64 s, screen stage 1 3.89 s, screen stage 2 2.87 s.
Seven seconds against half a second. Any question that merely failed to look
personal was routed through a photograph of a code editor and two models - ask
"solve this", then "what is Java", and Java went the slow way. A follow-up now
has to look like one: it either joins onto what came before (and, so, what
about) or points back at it (that, this, it). The first draft used a word count
and let "what is Java" straight through, because it is three words and "and the
time complexity?" is four. Length cannot separate them; standing alone can.
"Why" and "how" are deliberately not openers on their own, or "why is Java
slow" would read as a follow-up. Anything naming the screen still takes the
screen path first.

**The engine now emits the segment confidence it already computed.** `678ccdb`.
Average word confidence was calculated per segment and thrown away unless the
segment was dropped, so both clients were judging whether a transcript was real
by looking at its grammar. The Mac session measured why that cannot work:
scoring by function-word ratio put "Explain TCP three way handshake" at 0.20
and the worst non-English garbage at 0.22. They overlap, so any threshold
catching the noise also rejects a legitimate short technical question. They
reported the negative result instead of shipping it, which is why this fix
exists. Confidence measures the right thing - speech in another language
transcribed as English scores low precisely because the recogniser was
guessing. One line per segment, ">>> SEGMENT CONF: 0.42". Clients can now hold
a stricter floor for answering than the engine holds for displaying: showing a
doubtful line costs nothing, spending a credit on it is a decision that should
have evidence.

**The continuation chain was extending its own deadline.** `6c98dfb`. The
window was measured from the last submission, and a merge is a submission, so
every merge pushed the deadline forward and the chain could only end if the
speaker went quiet. Background speech never does. One API call and one credit
per fragment, the answer replaced mid-read, the question growing into a
paragraph of noise. The Mac session hit it first and measured roughly two
hundred credits in five minutes; Windows had it identically. Four independent
bounds now, so no single one has to be right: the window anchors to the chain
start which a merge never moves, at most two merges whatever the clock says, at
most sixty words merged, and a fragmented-noise test ported from the Mac
AutoTurnDetector. That last one refuses to judge below twelve words and five
stops, so a genuine "Okay. Sure." is never caught - my first two noise samples
were eleven words and were correctly refused, which is the floor working rather
than the detector failing.

**The Speechmatics session is closed before the engine is killed.** `2349f80`.
A killed engine never closes its websocket, so Speechmatics holds the session
slot until it times out server-side, and the account has a limit on concurrent
sessions. Every engine restart, every settings change and every app close left
a ghost holding a slot; the orphan cleanup at startup did the same, and that
one is worse, since an orphan has been holding a slot unattended since the app
last died. The shutdown.flag mechanism already existed and the engine already
exited cleanly on it - neither kill path used it. Both now write the flag, wait
1.5 s, and kill only past that. Found from the Mac side, which exhausted the
shared quota with the remains of its own test runs: with one account, a leak on
either platform starves the other, and nothing on either machine connects the
two events. A Windows leak silences a Mac user mid-interview.

**A busy account and a broken key no longer share an exit code.** `73f6bf6`.
They need opposite responses: a key is permanent and only the user can fix it,
a full account is temporary and clears the moment another device stops. Sharing
code 2 meant a busy account was reported as "fix your Speechmatics key in
Settings" - wrong, unactionable - and it stopped the retry loop, so the app
stayed dead after the other device finished and only a relaunch revived it.
Quota refusals now exit 4. The app says another device is using the account and
retries in twenty seconds. It also throws the cached token away on that
refusal, which is the part that cost hours: the minted token is cached to disk
for an hour and stays valid when the account behind it changes, so after the
key was swapped server-side both machines kept using a token pointing at the
old exhausted account. Deleting sttkey.json by hand was the only thing that
moved it. Mac caches the same token in the Keychain and had the identical
asymmetry - check that the Keychain copy is discarded on a quota refusal too,
or a correct server-side fix will again look like it did nothing.

**The plan's real limits are read out of the token the app already holds.**
`e21902b`. The Speechmatics token is a JWT and its claims carry the numbers
that decide whether the product works: connection_quota is how many people may
transcribe at once across the whole account, account_type says which plan.
Nobody knew the number. It is two - two customers, ever, simultaneously - and
it was found only after a day of chasing symptoms. It was sitting in every
token the app had ever received. The Mac session suggested reading it and it is
the best technique either side produced that week: no API call, no portal
access, no credential beyond a string already in memory, and it answers "what
are the real limits" for an account nobody can log into. Logged on every fetch
with an explicit warning at two or below. Never throws - a token whose shape
changes must not stop the app transcribing; this is information, not a gate.

**A month of interview audio was accumulating on disk.** `3e17264`. Every
session records itself. Nothing asked for it, nothing plays it back - the
Sessions panel only ever deletes these - and nothing removed them. Measured on
one machine after a month: 77 files, 457 MB, individual recordings up to 64 MB,
279 MB of it more than a week old. Two reasons that matters and the second is
larger: it is somebody's disk, and it is a month of their interviews kept
indefinitely by a product whose entire value is discretion, without anyone
having chosen that. Recordings older than seven days are now removed by the
sweep that already runs at startup, sharing its cutoff so there is one
retention rule rather than two. Transcripts are untouched - verified the
pattern matched 77 recordings and zero of 374 transcript files before shipping,
because a glob that deletes the wrong thing here destroys the feature the panel
exists for. `678ccdb` later pinned it to two explicit patterns, on the Mac
session's point that a rule which is safe because somebody checked is weaker
than one that is safe because it cannot match. Whether to record at all is a
product decision and was not made here - it is open on both platforms.

**Screenshots upload when the screen changes, not every two seconds.**
`a7d70e5`. The check meant to stop repeat uploads hashed the PNG bytes, and a
hash of exact bytes almost never matches on a real screen - a caret blinks, a
clock ticks, a counter changes, and a handful of pixels gives a completely
different hash. It never once stopped an upload. From a real session, every two
seconds without pause: 528 KB, 529 KB, 542 KB, 547 KB, 546 KB - roughly fifteen
megabytes a minute of the user's connection, for a screen nobody was asking
about. LastCaptureSignature already existed for exactly this distinction, 16x16
in sixteen greys, and is what tells scrolling from a ticking counter a few
lines further down. This may also be why answers felt slow: a question competes
for the same upstream as a 500 KB upload that had no reason to be in flight.

### The shape these keep having

Four faults that week read as correct and did nothing when run: a guard that
cannot fail (the byte-hash dedupe), a check that answers a narrower question
(grammar scoring standing in for confidence), recovery behind an unreachable
branch (the OpenAI third try on a lapsed account), and a bound that resets
itself (the continuation window). If a fix on the Mac side has one of these
shapes, it is worth running rather than reading.

### Still open on both sides

- Speechmatics is Free with a 2-session quota. This is a purchase, not a code
  change, and it blocks launch on either platform.
- The debug log is appended one line at a time with File.AppendAllText, opening
  and closing the file roughly ten times a second while listening. Bounded per
  run since it is truncated at launch, so waste rather than growth.
- The engine still has no versioned artifact. Unchanged from the note below,
  and still the thing most likely to make the two apps silently differ.

---

## 2026-09-11, after 1.0.18: one mode, and it listens to both

The biggest change on Windows since the nine fixes, and the one most worth
copying, because it deletes a setting rather than renaming it.

**Practice and Real interview are gone. There is one mode.** They were never
two modes: they differed in exactly one thing, which audio source was open -
the interviewer arrives through system audio, the candidate through the
microphone. Opening BOTH answers both, and the choice stops existing.

The choice was worth deleting rather than relabelling because getting it
wrong fails silently. A real interview left on Practice listens to the
candidate instead of the interviewer. Nothing errors, the mic ring lights,
the engine reports healthy, and no answer ever arrives - the user concludes
the product is broken. Three names were tried for this setting over the
weeks ("System audio only / + my voice", then "Real interview / Practice")
and renaming a trap does not disarm it.

**This was not safe before the read-back fix.** With the microphone open
during a real interview the app hears the candidate read its answer aloud and
treats it as a new question - the answer they are half-way through reading is
replaced by an answer to itself, and they are charged for it. `d391348` is
what makes one mode possible, so on Windows it is now load-bearing rather
than a nicety. If Mac merges the modes, that detector has to be there first.

`MicCaptureEnabled` survives as a Settings switch, not a mode: default on,
off means real interviews still work and practising alone does not, and the
card says exactly that instead of naming an audio topology. Nothing migrates
- same flag, different question.

The toolbar is now one control, AUTO | MANUAL, and it is only ever about WHEN
the app answers. It also stopped being a dropdown: a menu hides the state
until you open it, and this is state a user needs at a glance mid-interview.
Auto/Manual no longer restarts the speech engine either, since it cannot
change the capture mode any more - that restart was a visible stall on every
flip.

The Manual card states the real reason it is more accurate, which is worth
copying verbatim because it is true and specific rather than marketing: in
Automatic the app infers where a question ended from a pause, so an
interviewer pausing mid-sentence to think gets answered half-way. Space
removes the inference.

## Two measurements worth having on Mac

**The Speechmatics session close takes ~3.9 seconds.** Windows waited 1.5s
before killing the engine, which is less than half of what it needs, so every
single app close stranded a session slot until the server timed it out. The
number was a guess and it was wrong twice - once blamed on a recording being
flushed first, then observed again with nothing to flush at all.

Rather than pick a third number, the code now MEASURES it and logs the real
elapsed time on both paths. First run after raising the bound to 6s:

    [ENGINE] Engine closed its Speechmatics session cleanly in 3893ms.

"Cleanly" is the word that matters - before this it was always killed. A
longer bound costs nothing when the engine is quick, because WaitForExit
returns the moment it exits. If Mac kills its engine on a timer, check the
number against 3.9s.

The orphan-cleanup path deliberately keeps its short 1.5s bound, and the code
says why: it runs synchronously in the MainWindow constructor before the
window is shown, so every millisecond is a frozen startup, and six seconds of
white screen to reclaim a slot that has ALREADY been leaked since the last
crash is the wrong trade. It cannot be made async either - the shutdown flag
must be written before our own engine starts, or the new engine reads it and
exits immediately.

**The screen was being captured every two seconds while muted.** ~500 KB
captured and encoded, several hundred milliseconds of CPU, for a question
nobody can be asking while the microphone is off. The only guard was "the mic
was used within five minutes", so one press of Space bought five minutes of
capturing whatever was on the screen - about a hundred and fifty captures of
somebody's private desktop that nothing would use.

Now fifteen seconds while muted, two while listening, and unmuting prepares
one directly rather than waiting for the next tick - which is what lets the
muted interval be long without costing the first question its screenshot.
Separate from `a7d70e5`, which stopped the UPLOADS; this is the capture
underneath them, which ran regardless.

---

## 2026-09-11, evening: an enterprise pass on visible text

The owner reads certain patterns as "AI generated", has now said so for both
the website and this app, and they are cheap to avoid on Mac from the start.

**Keep out of anything a user sees:** em and en dashes, middle-dot separators,
letters typed with spaces to fake tracking ("A I   A N S W E R"), marketing
taglines under the product name ("Interview Intelligence", "tailors every
answer"), glossy highlights, coloured glows, and emoji used as icons in the
toolbar. WPF has no letter-spacing property, which is why the fake-spaced
labels existed; SwiftUI has `.tracking()`, so Mac never needed the hack, but the
taglines and dots are worth checking for.

**The model picker was removed.** It offered "Fast" and "Accurate (GPT-4o)".
The backend chooses the model itself and ignores the provider label the client
sends, and the OpenAI account behind GPT-4o has been inactive since 2026-08-22,
so the card promised something nobody could get. If Mac still shows a model
choice, it is equally fake.

**A status message must never live only in a tooltip.** When the Auto/Manual
pill became a two-segment switch, its notice text moved into a tooltip, and
nine warnings went invisible at once, including "Another device is using your
account" and "Not transcribing. Restart the app." Nobody hovers a tooltip. The
message now replaces the switch for four seconds, the way it replaced the old
pill's label, and the wording is sentence case rather than shouted capitals.

**Smaller changes, all visible:** the credits badge reads "5.0k credits, 21h 53m
left" instead of emoji; keyboard shortcuts are drawn as key caps instead of a
sentence joined by dots; the sessions list date is "Sep 11, 2026, 4:05 PM"; and
long Settings descriptions were cut to one or two sentences while keeping every
fact they carried. Two of those facts were dropped by mistake in the first draft
and put back (the F7 shortcut, and "support varies" on stealth mode), which is
the reason to compare old and new text line by line rather than rewriting blind.

---

## 2026-09-11, late: screen answers, measured

**Backend, shared, so Mac gets it without a client change** (commit `773b121`).
A screen answer is two model calls: stage one reads the screenshot into text,
stage two writes the answer from that text. Stage two never sees the image, so
it moved from Gemini to Cerebras gpt-oss-120b. Measured from the server, same
prompt, first token / full answer in ms, five runs each:

    cerebras gpt-oss-120b   166/246   236/289   335/411   113/166   151/209
    gemini-3.5-flash-lite   653/1165  698/1052  601/953   604/960   522/928

Full answer time is the number that matters here, not first token: the backend
buffers the whole coding answer to repair pointer signatures before sending
anything. Checked against the REAL 2,864-character stage-two prompt on three
problems before shipping, all passing SAY THIS / DETAIL / balanced fences / not
truncated, including Insertion Sort List with a value-typed `ListNode head`,
which Cerebras rewrote to `ListNode* insertionSortList(ListNode* head)` itself.
Stage two has its own 1800-token cap now, because gpt-oss spends part of the cap
reasoning and a class cut off mid-method does not compile.

Stage one, the verbatim copy of the problem statement on Gemini, is now the
larger share of the wait. It is left alone on purpose: it is what the code is
written from.

**Windows client: a staleness window shorter than its capture interval.**
Prepared screenshots are taken every 2s but were only usable for 1.5s. For the
last half second of every cycle the shot had already expired, so a question
asked then, about a quarter of them, captured the screen again while the
candidate waited AND uploaded the full image inside the request instead of the
id already on the server. The comment beside the value argued for four seconds
and the number never matched it. Now 2.5s, one interval plus a capture. If Mac
prepares screenshots on a timer, compare its staleness limit with its interval.

---

## 2026-09-11, night: an error handler that only printed

**This one is in the SHARED engine, so it is Mac's bug too, in the same lines.**

`speechmatics_engine.py` registered this:

    ws.add_event_handler("Error", lambda e: print(f">>> WS ERROR: {e}", flush=True))

A Speechmatics Error ends the session: the server stops listening and closes.
The handler printed it and nothing else in the process ever learned. Captured on
a real machine, from the owner's own log:

    13:04  WS ERROR: idle_timeout, no audio sent within the last 1h0m0s
    ...    seven hours of HEARTBEAT lines, process alive, socket dead
    20:00  user presses Space and waits ELEVEN SECONDS

The engine kept running, kept reading the microphone, kept heart-beating into a
connection nobody was reading. The auto-reconnect loop that has always been
there never ran, because nothing raised. The first thing to notice was a person
pressing a key.

Fixed by recording the error and letting the read loop raise it, which is
exactly what the shutdown flag already does. It leaves through `ws.run()` into
the existing `except`, where the text is classified as it always was: a bad key
still exits 2, a full account still exits 4, and a drop like idle_timeout takes
the retry-with-backoff path. Cleared before every connection attempt, or the
first drop would make every reconnect raise at once.

If Mac ships its own copy of this file, it has the same dead handler.

**Windows side: the kill path made the user wait for a corpse.** The graceful
close added this morning waits up to 6s for the engine to close its session, and
a hung engine can never answer, so the wait always ran out in full. Measured in
the same incident: 6003ms of an eleven second delay, spent while the user was
pressing Space. It now waits only when the engine is actually online, because a
disconnected engine has no session to close politely. The 6s stays for the live
case, where a real close was measured at 3.9s.

Rebuilding the bundled engine is required for this to reach users:
`powershell -ExecutionPolicy Bypass -File toolsuild-engine.ps1`, then build the
app. The Windows binary shipping today is a PyInstaller build, so editing the
.py alone changes nothing a user runs.

---

## 2026-09-11, audit: two faults found by auditing, both silent

**The speech vocabulary was being erased on every engine restart that fetched a
key.** WriteVocabFile builds a word list from the resume, company and job
description and writes vocab.txt, which the engine feeds to Speechmatics as a
custom dictionary. It reads the resume from a WPF TextBox, and it is called from
StartSpeechmaticsEngine AFTER this line:

    await UserSession.EnsureSpeechmaticsKeyAsync(...).ConfigureAwait(false);

ConfigureAwait(false) abandons the UI thread, so the rest of that method runs on
a thread-pool thread, where touching the TextBox throws. The catch was:

    try { resume = ResumeTextBox?.Text ?? ""; } catch { }

From the owner's log, same app, same day:

    12:04  Wrote 170 interview terms     key cached, no await, UI thread
    20:00  Wrote 0 interview terms       key fetched, await, pool thread

Zero terms costs the recogniser the candidate's name, their technologies and the
company - precisely the words it gets wrong - and it reported "Wrote 0" as though
that were a normal result. The resume is now read through the Dispatcher, and an
empty vocabulary logs what was missing instead of looking like success. If Mac
builds a vocabulary the same way, check which thread it reads the UI from.

**A rejected token signed the user out permanently, mid-answer.** On HTTP 401 the
answer path called UserSession.Clear(), which wipes the refresh token and deletes
the session file, then switched to guest. A paid plan became guest credits in the
middle of an interview, recoverable only by logging in again.

It could not have been avoided by refreshing, because TryRefreshAsync opened with

    if (!IsTokenExpired()) return true;

IsTokenExpired is the CLIENT's opinion: a saved timestamp under 55 minutes old.
When the server rejects a token the client still believes in - a revoked session,
a slept laptop, clock drift - the refresh returned "true" having done nothing, so
the token was unrepairable by construction and destroying the session was the
only path left. The code even had a comment naming this outcome; the mitigation
beside it called the same unusable refresh.

TryRefreshAsync now takes force, which skips that check, and refusals are logged
rather than silent. A 401 forces one refresh and retries once; the sign-out
happens only when the refresh itself is refused, which is the only real evidence
the session is gone. Mac shares this backend and this Firebase flow, so if it
clears a session on 401, it has the same defect.

---

## 2026-09-11, night: why the answers sounded like a textbook

The owner's words: the answers are "very robotic and difficult to read like a
real candidate is speaking in real interview". He was right, and the cause was
not the model. It was our own prompt arguing with itself.

`AppendSharedVoiceRules` tells the model to say what a thing is for and where
you have met it, and carries a worked Java example doing exactly that. Then
`BuildFormatReminder`, for a simple definition question, said:

> Do NOT mention your background, job, project, company, or personal experience
> unless the interviewer asked about it.

Two rules pointing opposite ways. The format reminder wins, because it is
placed in the user message directly above the question, which the code's own
comment says is where the model looks hardest. The production transcript shows
what came out:

> "Java is a statically-typed programming language that runs on the Java Virtual
> Machine, letting the same compiled code execute on any platform with a JVM."

**How this was fixed, and the part worth copying.** The prompt was reconstructed
from the C# source, sent to the live Cerebras model, and scored on one thing:
does the first sentence open by classifying the term. Ten definition questions
per variant.

| reminder text | opens "X is a ..." |
|---|---|
| what shipped | 8 of 10 |
| ban the bare term | 3 of 10 |
| name the shape, and ask for an action verb | **0 of 10** |

Two details decided it. The ban has to describe the opening mechanically and
say a leading "A" or "An" does not exempt it, because banning only the bare
term still produced "A hash map is a key-value store". And it has to ask for an
action as the main verb, not merely forbid "is", so the model has somewhere to
go: "A hash map gets you a value back in roughly constant time."

**The mistake to avoid.** A first draft scored 0 of 4, was reworded slightly
while being pasted into the C# and never re-measured. Measured properly, that
reworded string scored 8 of 10. Test the exact bytes that ship; a prompt is not
code you can reason about.

Mac builds its own copy of this reminder. No Mac source was reachable from
the Windows machine, so this is a thing to check, not a thing checked: open
`PromptBuilder.swift`, find the branch for a simple definition question, and
see whether it still forbids mentioning your own experience while the voice
rules ask for it. The measurement harness is worth rerunning there rather
than trusting these numbers, since the Mac prompt is assembled differently.

## 2026-09-11, night: the compact overlay showed the wrong end of the answer

Same report, second half: "in the compact it is messy and weird".

`AnswerWindow.UpdateAnswer` called `AnswerScroller.ScrollToBottom()` on every
update. That is right for a chat log and wrong here. This overlay is what the
candidate reads out loud, the words they need are the first ones, and the
scroller caps at 340px, so by the time an answer finished streaming they were
looking at the MORE TO SAY bullets with the spoken answer scrolled off the top.
It now scrolls to the top, and only when a new answer starts, so a reader who
has scrolled is not yanked somewhere else mid-sentence.

The second half of "messy": the spoken answer and the follow-up bullets were
one TextBlock at one size, so the literal words MORE TO SAY printed as a
sentence in the middle of the answer and the bullets looked exactly as urgent
as the words being spoken. They are now two blocks: the answer at 16pt
semibold, then a quiet 10pt label and the bullets at 13pt in a dimmer grey.
The blank line that used to separate bullets is gone, since line height now
does that job and the gaps cost 60px of a 340px budget.

Both were verified by rendering the real `AnswerWindow` out of the built
assembly into a PNG and looking at it, not by reading the XAML.

## 2026-09-11, night: the non-breaking hyphen

`CleanAiOutput` already rewrites em and en dashes, which the owner reads as the
AI-generated look. It matched `[—–―]` and so never saw U+2011, the
non-breaking hyphen, which this model reaches for constantly in compound
adjectives: "statically‑typed", "key‑value", "real‑time",
"self‑healing". It renders as a hyphen that refuses to wrap, which drops a
long compound onto its own line in the narrow overlay. U+2011, U+2012 and
U+2212 now normalise to a plain hyphen.

`CleanAiOutput` was made `internal static` so the test project calls the
shipping function rather than a copy of its rules, and `HumanVoiceTests` covers
all of it, including that hyphens inside a fenced code block still survive.
That suite opens with a self-check proving it can see a non-breaking hyphen at
all, because a sweep for exactly these characters was once written in a shell
heredoc that collapsed the backslashes, reported zero for a file containing
eight, and the zero was passed on as clean.

---

## 2026-09-12: the backend and the website changed under you

All of this is deployed and live. The Mac app talks to the same backend, so most
of it is already yours without a line of Swift.

### Every billable action is now recorded

There was no record of a charge anywhere. `deductCredits` moved two numbers on
the user document and printed a line to stdout, so the product could bill
somebody and never say why. A new collection, `usage_events`, now takes one row
per action: who, what (answer / screen / resume), provider, model, credits, and
the real prompt and completion token counts.

Written inside `FirestoreCreditsService`, which every charge already passes
through, so no caller can forget. It never blocks a request: rows go on a
bounded background queue and a full queue drops the row rather than adding
milliseconds to an answer. It never fails one either; every path swallows its
own exceptions.

Nothing is needed from the Mac side. The rows appear because the backend writes
them.

### Token counts, and a trap in getting them

Both Cerebras and Gemini report tokens only when the request sets
`stream_options: {"include_usage": true}`. Verified accepted on both before
wiring, because that endpoint rejects unknown fields outright and a rejected
request is a blank answer on somebody's screen.

Two things worth knowing if the Mac client ever parses provider streams
directly:

- Cerebras puts usage on a chunk whose `choices[0].delta` is empty, with
  `finish_reason`.
- **Gemini puts usage on a chunk that also carries content.** It does not follow
  the OpenAI convention. A naive "drop the usage chunk" rule deletes words out of
  the middle of an answer.

The backend now captures that chunk and does NOT relay it, so no client sees a
new shape. The Windows parser was checked and ignores an empty `choices` array
safely; the Mac one could not be checked from the Windows machine. **Check
it anyway**: find where the Mac app reads SSE and confirm an empty `choices`
array returns "no token" rather than throwing.

### A credit bug that gave away free usage

Found while auditing before deploy, and it predates all of this.

On `/ask`, if the charge failed for want of credits, the code wrote an error and
returned. The `finally` block then refunded, because no answer had been
delivered. Nothing had been taken, so the refund was a grant: `refundCredits`
adds the cost back, capped at the plan allowance. An account sitting at zero
could press space, be told it had no credits, and be handed five. Repeat and the
meter never runs out.

Fixed: a refund now requires a charge to have actually happened. The screen path
never had this, because it charges synchronously and returns 402 before the
stream body exists.

If the Mac app has any client-side refund or retry logic around a 402, look at
it with this in mind.

### Answers on the website now use the same model as the apps

`app/api/stt/tokens/route.ts` on the website is what the mock-interview and
resume pages call. It still had a full Groq client and defaulted to it. Groq is
gone from the account, so that route now calls Cerebras `gpt-oss-120b` with
`reasoning_effort: "low"`, the same as the Java backend.

A note on how that was nearly missed. The route was first judged dead because a
grep of the website container's logs for its path returned zero. The production
Next.js container logs no request lines at all, so that zero could never have
been anything else. The rule from the last time this happened still holds: prove
the detector fires on something you know is there before believing a zero.

### Measured latency, end to end

From the server, three runs each, time to the first VISIBLE word rather than the
first chunk, because gpt-oss streams hidden reasoning first.

| stage | measured |
|---|---|
| owner machine to server | 82ms warm, 160ms cold |
| answer, Cerebras gpt-oss-120b at effort low | **129ms** |
| answer, Gemini 3.5 flash-lite | 458 to 523ms |
| screen read, Gemini 3.5 flash-lite | 770 to 1276ms |
| Speechmatics | 700ms, the API's hard floor |

An answer is about 210ms end to end to the first word. There is no easy win
left: `gpt-oss-120b` with no `reasoning_effort` never reached a visible word at
all, `qwen-3.8-27b` is also a reasoning model with the same problem, and
`gemma-4-31b` is not accessible on this account (its "59ms" was a
`model_not_found` error, not speed).

Screen reading is the slow stage and it is Gemini's latency, not ours. Shrinking
the screenshot does not help: 640px measured slower than 1280px, which is
variance rather than payload. Cerebras has no vision model, so the only lever is
a different vision provider.

### Provider truth in user-visible text

The site claimed Groq in places the product no longer uses it. Corrected: the
privacy policy listed Groq and omitted Cerebras, which now processes every
spoken answer; the terms named Groq; the homepage said "Groq LPU inference"; the
resume page offered a Groq button that silently resolved to Gemini.

If any Mac screen names a provider, check it against Cerebras for answers and
Gemini for vision.

### OpenAI is removed (corrected 2026-09-14)

An earlier version of this section said the OpenAI key was not dead. It was.
The account has no billing and answers every request with
`429 billing_not_active`, and two website features still defaulted to it, so
both failed on first use: the mock interview (default model `gpt-4o`) and the
resume tailor (default provider OpenAI, which `ResumeTailorService` skipped
only when the key was missing, never when it was unfunded).

OpenAI is now out of the product. The backend has no third-try OpenAI fallback
and the tailor always uses Gemini. The website route maps the `gpt-4o` ids onto
Cerebras, the pickers no longer offer GPT-4o, and the privacy policy no longer
lists OpenAI.

Two strings must stay accepted on the backend allow-list: `"groq"` and
`"openai"`. Installed Windows builds send `"openai"` for anyone who once chose
GPT-4o (`MainWindow.xaml.cs`, `ScreenAnalyzer.cs`). Removing either rejects
already-installed copies. **If the Mac app sends a provider string, check
which one**, and do not remove it from the backend.

Retired by Google and remapped the same day: `gemini-1.5-flash` and
`gemini-1.5-pro` both answer 404 "not found for API version v1beta". The
live-interview picker offered Gemini 1.5 Flash; it is gone, and both ids now
resolve to `gemini-3.5-flash-lite`. If a Mac setting names a Gemini 1.x or 2.0
model, it is dead.

### Admin portal

Rewritten around the new data: per-user credits, answers, screen reads, tokens
and provider cost, against what that user pays. Revenue, refund rate, a
credits-per-day chart, and a live activity feed.

Cost is deliberately not calculated yet. `data/providerRates.ts` ships with
every rate null, and the portal shows "not priced" rather than a number invented
from a price list nobody checked. Token counts are measured and always shown.

`ADMIN_EMAIL` was never set on the server, which meant the admin API refused
every request and the portal had never worked for anyone. Set now.

---

## Where the reasoning lives

The Windows commit messages, `git log` on `windowsNative`, one commit per
fault, each explaining what broke and why the fix is shaped that way. That
history is the point: several bugs today were caused by earlier fixes, and
without knowing why a number was chosen the next session will change it back.

`MAC_PARITY.md` in the same repo is the older feature-gap list and is
**unverified** — it was built by grepping Windows class names against a stale
Mac copy. Treat it as a prompt for questions, not as fact.
