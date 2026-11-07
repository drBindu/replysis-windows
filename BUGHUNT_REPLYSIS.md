# Replysis bug hunt

A running list of bugs and security holes found and fixed, one section per round, in plain words. Each round covers
one area across the Windows app, the website and the server. Fixes are local commits until the owner says to release
or deploy.

## Round 1, 2026-11-07: sign in and Google sign in

Checked: the Windows sign-in window, email and Google sign-in, the saved session and its refresh, sign-out, the
website sign-in box, the session cookie, the page guard, password reset, sign-up with the 1 day trial, the server's
sign-in check and the Google code exchange.

Fixed:

1. **Website: a link could send a signed-in visitor to another site.** The page that opens after sign-in came from
   `?next=` in the address, and the check only refused a value starting with two slashes. `/\evil.com` and
   `/<tab>/evil.com` passed it, and the browser reads both as `//evil.com`. A link like
   `replysis.com/?auth=signin&next=/%5Cevil.com` would take a signed-in visitor straight to that other site, which
   is how phishing pages borrow a trusted address. The path is now resolved the way the browser resolves it and must
   stay on replysis.com. Test: `scripts/check-safe-path.mjs`.
2. **Windows: a wrong password could email the owner an outage alert.** Every sign-in message was sent to admin
   as a real app error, and two error reports from two addresses within ten minutes send the "apps are reporting
   errors" email. Two people mistyping a password was enough. Typing mistakes (wrong password, unknown email, bad
   email, short password, email already used) are now sent marked minor, which admin still shows but never alerts
   on. A disabled account, a lockout and real failures still count.
3. **Windows: closing the window right after a successful sign-in reported a failure.** The welcome message waits
   0.8 s before the window closes. Closing it during that pause cancelled the wait, the cancellation was caught as a
   sign-in error, and "could not sign in" (or Google "E0") was reported to admin although the person was signed in.
   The pause now ends quietly, and a closed window reports nothing.

Code quality, low risk:

4. **Windows: sign-out now deletes the saved speech token** too, not only the session file. Nothing reused it (the
   next sign-in replaced it), but it belonged to the account that signed out.
5. **Windows: a sign-in refresh overtaken by a different sign-in is no longer called "refused".** "Refused" is
   what signs a person out, so a refresh that lost a race could have signed out whoever had just signed in.

Checked and fine: Google sign-in uses PKCE and checks the state value; the callback listener only accepts this
attempt's answer; the server only accepts a 127.0.0.1 redirect and keeps the Google secret; the saved session and
speech token are encrypted for this Windows user; the website session cookie is httpOnly, SameSite strict and
verified on every protected page; the server checks the token on every request.

For the owner to decide: anyone can make unlimited new accounts with throwaway emails, and each gets the 1 day
trial (50 answers). Stopping it needs a rule such as a few trials per address a day or a verified email before the
trial, and either can turn away real people on a shared office or mobile network.

## Round 2, 2026-11-07: credits and plans for a brand new Free user

Checked, in order: a guest before sign in, a new account getting the 1 day free trial (50 answers), the trial
running, the reminder and follow up emails, the trial ending, the account after the trial with no answers, running
out of answers in the Windows app (voice, F8, typed), the badge, the credits window, the Account page, the upgrade
buttons and the one free interview with Maya. Numbers were compared across the Windows app, the website and the
server.

Fixed:

1. **Windows: a free trial customer was shown "pro plan" and "500 answers each month".** On the server the trial
   runs on the Pro plan, and the server's answers reply has said "trial": true since 2026-10-27 so the apps can tell.
   The Windows app never read it. A trial customer saw "pro plan" in the profile menu, "Pro plan, 500 answers each
   month" with a refill date in the answers window, and when the 50 answers ran out the message said "No answers left
   this month ... until they renew" with no upgrade button, because the app thought they were paying. The app now
   reads the trial flag: the trial is called "Free trial", the answers window says "50 answers to try everything" and
   "Not refreshed", and running out shows "Your free answers are used" with the button to the plans.
2. **Windows: a signed in free account was told "5 answers, one time".** Since the trial replaced the 5 free answers,
   an account whose trial has ended holds 0 answers, and the website removed that line (owner, 2026-11-02). The
   answers window now says "Free answers do not refresh", as the website does. A guest, who does get 5 free answers on
   the device, still sees "5 answers, one time".
3. **Windows: plan names were shown in lower case** ("pro plan", "free plan"). They now read like the website:
   "Free trial", "Starter plan", "Pro plan", "Max plan", "Practice plan".
4. **Website: a trial that ran out of time got the email "You have used all your answers".** The out of answers email
   picks accounts with almost no answers left that used at least one. A 1 day trial that ends leaves 0 answers, so
   anyone who asked even one question was told they had used them all, which was not true. Those accounts now get
   "Your free trial has ended" instead, with the same plans link. One email per person, as before.
5. **Website: the upgrade emails could start "Hi User".** "User" is the placeholder name before sign up finishes. The
   follow up email already dropped it; the upgrade emails now do too.

Guard added: the website's sync check now fails if the trial size differs between the website, the server and the
Windows app, or if the Windows app stops reading the trial flag.

Checked and fine: the website and the server create the same trial (1 day, 250 credits, 120 listening minutes); the
trial end moves the account to free with only bought answers kept and no monthly refill; the 4 hour reminder only
goes to trial accounts; the website badge and Account page call the trial "Free trial" and the ended trial "Starter";
the one free Maya interview is claimed once in a transaction.

Not tested by running: the emails were changed in code only (nothing was sent). The Mac app may read the trial the
same way the Windows app did; it is noted in MAC_CATCHUP.md.

## Round 3, 2026-11-07: listening and audio devices

Checked: the Windows sound capture (microphone and the computer's sound, default device changes, a headset or
Bluetooth connecting, devices that will not open or stop by themselves, 8 kHz Bluetooth mics, 44.1 and 48 kHz
speakers, 16, 24 and 32 bit and float formats), the pipe to the speech engine and how the engine reads it, mute and
unmute, the mix of microphone and computer sound, the listening meter and its reports to the server, presence, and
the speech pass from the server.

Fixed:

1. **Windows: listening time was lost whenever its report failed.** The app reports listening time to the server
   every few minutes, and a report that failed was simply dropped. A sign-in that had just expired (401), the server
   restarting during a deploy (502 to 504), a busy reply (429) or a connection that never opened meant those minutes
   were never counted, so the fair use limit under-counted long sessions. Those minutes are now kept and sent with
   the next report. A timeout or a lost reply is not kept, because the server may already have counted it and
   charging twice is worse than missing a minute; kept time never piles up past an hour. Tests in BillingTests.

Checked and found fine, with numbers where it mattered:

- The computer's sound is read without waiting, at most one chunk at a time. A model of real timing (10 ms speaker
  packets, a microphone paced at 100 ms with jitter and the occasional 40 ms stall) put the interviewer's sound at
  10 ms behind on average and 60 ms at worst, so no change was needed.
- The engine side reader empties the pipe all the time, so a slow engine can never block the capture thread.
- A device that fails to open or stops is reopened every 5 seconds with nothing leaked (fixed 2026-11-04).
- 8 kHz, 44.1 kHz and 48 kHz devices, mono to 8 channels, and 16, 24, 32 bit and float samples all convert to 16 kHz
  mono correctly.

Noted, not changed: if Speechmatics is down at the server AND Deepgram refuses the connection, the engine falls back
to the placeholder Speechmatics key, is refused, and the app renews the speech pass and restarts listening. That is
a retry loop only when both providers fail at once; before 2026-11-04 listening could not start at all in that case.

## Round 4, 2026-11-07: the answer engine

Checked: how a heard question becomes an answer in the Windows app (the wait for the last words after Space, Auto's
"answer now", the canned replies to hello and small talk, the prompt and its history and context limits, the request,
the 401 refresh and retry, the stream reader, the 20 second first word limit, cancelling with Space, the answer
history and the compact window) and the server's answer path (the charge running alongside the model, the backup on a
server error or a rate limit, the race against a silent model, the held back opening that catches refusals, refunds
when nothing reached the person, and charging once 120 characters have been read).

Fixed:

1. **Windows: running out of answers emailed the owner as if the app were failing.** The server charges for an answer
   inside the answer stream, so "You have no answers left." arrives there, not as a 402. The app reported it as an
   ordinary answer failure, and the server counts those towards the "apps are reporting errors" email. Every customer
   reaching the end of their answers, two of them within ten minutes, was enough. It is now reported as "nocredits",
   which the server's alert rule never counts, on both the stream path and the 402 path.
2. **Windows: a free trial that ran out in the middle of answering got no Upgrade button.** Round 2 added the trial's
   Upgrade banner to the 402 path and the check before asking, but the usual way the server says it is inside the
   stream (the balance on screen can lag by one answer when Auto answers twice quickly). That reply now gets the same
   banner, and both paths show the same words: "You are out of answers. Open the answers badge at the top to add more."
3. **Windows: the 20 second first word limit was logged as a bare TaskCanceledException.** It is now named in the log
   and reported as "answer-first-word-timeout", so the admin panel says what actually happened. A stream cut by the
   connection's own limit after words had arrived is not mislabelled as this. No change to what the person sees.

Tests in PlanFactsTests (the server's words are recognised, a real fault is not mistaken for them, the report name).

Checked and found fine:

- The wait for the last words after Space is capped at about 1.3 s and only stretched while the speech service is
  provably behind; nothing to cut without losing the end of questions.
- The prompt is bounded: four turns of history, 4,000 characters of question, 12,000 of resume facts, and older code
  collapsed. History is only touched on the window's own thread.
- The canned replies subtract the pleasantry and answer only when nothing else was said, so "How are you handling
  state in React?" still reaches the model.
- A 401 is refreshed and asked once more; auth failures are not charged, so the repeat is not a second charge.
- The server answers from the backup at once on a 5xx or 429 when one is configured, races a silent main model after
  the hedge delay, refunds anything that never reached the person, and keeps the up front charge off the first word.

Noted, not changed: the server holds the first 64 characters of every answer to catch a model's refusal before the
person sees it. That costs the time to generate those characters (tens of milliseconds on the fast models) on every
answer. Changing it is a product decision, because it trades speed against an apology occasionally reaching the screen.

## Round 5, 2026-11-07: screen share and stealth

Checked: how every Replysis window and popup is hidden from screen sharing (the per window setting, the guard that hides
any new window the moment it shows and sweeps every second, native dialogs), what happens on computers where Windows
refuses full hiding (the black box fallback, the quiet Screen share row, the problem reports and their filters), the
screen reading path (F8, the Analyze button, the select area picker, reading while listening, which is on purpose and
stays), which window and which screen is read, and what a problem report can carry.

Fixed:

1. **Windows: on a computer that can only show Replysis as a black box, every screen read took the black box away.**
   Before reading the screen the app hides its own windows from that one capture, then undoes it. On a computer where
   Windows refuses full hiding (three Windows 10 laptops so far), the window carries the black box instead. The read
   tried to hide it, Windows refused, and the undo then cleared the setting entirely, so the answers were plainly
   visible in the interviewer's screen share until the once a second guard put the black box back. With F8 or reading
   while listening, that was up to a second of visible answers after every read. A black box window is now never
   touched by a screen read, and a read that changed nothing undoes nothing.
2. **Windows: turning stealth on during a screen read could leave the window visible.** The undo cleared the hiding it
   had put on, even if stealth had been switched on meanwhile. It now leaves the window hidden whenever stealth is on.
3. **Windows: on two screens, pressing Analyze read the wrong screen.** Pressing the button brings Replysis to the
   front, so the app reads the last window the user worked in, but it then clipped that window to the screen holding
   Replysis. With the work on the other screen nothing was left, and it read Replysis's own screen instead. Watch
   screen had the same mistake whenever Replysis was clicked. Both now use the screen of the window being read.

Tests in StealthCaptureTests (what a read does to a hidden, black box and visible window, the undo rule, and which
window's screen is read).

Checked and found fine:

- The guard skips other processes' windows and windows with no area, and logs a refused window once.
- Problem reports carry only allowlisted technical lines, never screen words or speech.
- Screen words are logged by length only, never the text.
- The select area picker closes before the capture.

Needs a real run before release: the black box case can only be seen on one of the computers that refuse hiding; the
two screen case on a laptop with a second monitor. Both are covered by the tests above, not by watching a screen share.

## Round 6, 2026-11-07: settings

**Fixed**

1. Saving Settings reset settings that are not on the Settings form. The Save button built a brand new settings object instead of starting from the saved one. So every save:
   - turned Keep on top back on for someone who had unpinned the window,
   - turned Detailed answers back off,
   - dropped the marker that says the one time move to 30% opacity already happened, so on the next start the opacity the person had just chosen went back to 30%.
   Now Save starts from a copy of the stored settings and only changes what is on the form. Because it is a copy, a failed save leaves the settings in use untouched, as the "Nothing was changed" message says.
2. A new install lost its first opacity choice after a restart for the same reason: the default settings had no 30% marker. Fresh settings now carry it.
3. Opening Settings while the chosen microphone was unplugged and pressing Save forgot that microphone, so after plugging it back in the app used the Windows default. Save now keeps the saved microphone when none is picked in the list.

**Checked and fine:** the settings file is encrypted with Windows' per user protection and written to a temp file then moved into place, so a crash mid write cannot leave half a file; a file that cannot be read falls back to defaults with stealth on and the microphone off; the backend address only accepts replysis.com, the old domain or loopback; the language picker falls back to English; the remote app-config.json only switches native audio on or off and passes whole numbers to REPLYSIS_ engine settings that the engine clamps.

**Tests:** SettingsSaveTests (6 checks) in CleanerTests.

## Round 7, 2026-11-07: speed (time to first words)

Real numbers from the last 14 days of answers, measured on the server from the moment a question arrives to the first words sent back:

- Voice answers (222 of them): typical 0.29 s, 9 in 10 within 0.76 s, slowest 2.1 s. Prompt size makes no difference. The few slow ones are the answer model itself being slow to start; the server already asks the backup model when the main one is silent for 0.9 s.
- Screen reads (50): typical 2.8 s, 9 in 10 within 3.7 s, slowest 4.3 s. This is the slow path. Almost all of it is the picture model reading the screen (about 1.4 to 3 s); charging the answer takes about 0.1 s before that.
- The app side was traced too: the answer connection is kept warm every 25 s (every 8 s while listening), so the first question does not pay for a new connection; after Space the app waits only until the last words have landed, usually 0.1 s, at most 1.28 s when the sentence clearly is not finished.

What was fixed:

1. Voice answers were saved without saying which app asked them (every one showed as an unknown app and version), because the charge runs on a separate thread that had no request to read the app from. Screen reads were fine. Now the app and version travel with the charge, so speed can be compared between app versions after each release. No effect on speed itself. Backend commit, needs a deploy.

Looked at and left alone, on purpose:

- Running the screen read's charge alongside the picture model (as voice answers already do) would save about 0.1 s of the 2.8 s, but it changes how "no answers left" reaches the app on screen reads and starts a paid picture read before the money is confirmed. Not worth the risk for 4 percent.
- The 64 letter hold before first words: kept, owner's decision.
- Making screen reads faster for real means a faster picture model. That changes answer quality, so it is the owner's call and needs a side by side test first.

## Round 8, 2026-11-07: updates

**Fixed (2):**
1. When an update was found, the top notice said "Replysis 1.0.x is on its way ... a Restart button appears here in a moment". If the download then failed (a dropped line, a full disk, antivirus holding the file), no Restart button ever came and that notice stayed up for the rest of the session, promising something that would not happen. Now, when a check ends with nothing ready, the notice is taken down quietly and any speech problem notice it covered comes back. The next check (10 minutes later) announces the version again.
2. A failed update download was reported to the server as a real error, so two customers whose download failed within ten minutes (for example during a short GitHub hiccup) emailed the owner "apps are reporting errors", though nobody's app had stopped working. It is now reported as minor: still visible in the admin Live panel, never an outage email.

**Checked and fine:** the Store copy never runs the Velopack updater (the channel comes from the package identity, and Velopack only acts on its own installs); nothing installs while the app is open, and Restart and update goes through the normal close, which asks first during a session; checks queue behind each other instead of failing; a stalled download is cancelled and retried; versions are compared as numbers, not text; the update feed is the public latest release only, so a draft or prerelease never reaches customers; the speech engine is stopped before the app exits, and Velopack's own updater closes anything still running from the install folder before it swaps files.

**Left as is:** the release workflow builds unsigned when no signing secrets are set and says so in the log. That is the current decision (signed .exe deferred), not a bug.

**Tests:** 4 new checks in UpdateTests (update policy).

## Round 9, 2026-11-07: the website pages

Fixed (website commit 30c8d9f3, local, needs a deploy):
1. Resume PDF lost text. The builder shows resume text as text, but the PDF was built by pasting the text straight into a web page. A bullet like "Built the <Header> component" came out as "Built the component", and "&nbsp;" or "&copy;" turned into symbols. Every resume field is now escaped once before the PDF page is built, so the PDF matches the preview. It also stops resume text being read as page code inside the PDF service. Check: scripts/check-resume-text.mjs.
2. Two middle dots in visible text: the filler words line in the web interview report, and the reviewer line on /proof. Now commas.

Checked and fine: raw HTML on the site is only fixed styles, the template thumbnails and the question page data, none of it from other people; answers from the AI are shown as text; the balance updates live after a purchase; paid buttons on the resume page are blocked while busy; the admin API checks for the owner's verified email; the resume draft kept in the browser is stored per account.

Skipped:
- The old /real-interview/popup page reads a resume from its address. Nothing links to it any more; it is dead code, worth deleting later.
- Signing out leaves the resume draft in that browser (stored under the account, never shown to another account). Clearing it on sign out would lose edits not yet saved, so it was left.
- The resume template list has middle dots between email, phone and place. That is the customer's own resume layout, not Replysis text.

Checks: npx tsc --noEmit clean, check-sync ALL IN SYNC, check-resume-text and check-safe-path ok. No browser run.

## Round 10, 2026-11-07: the backend API and the website's other API routes

**Fixed (server, commit in uiii/backend, needs a deploy):**
- **Free screen reads for accounts with nothing left.** While listening, the app sends the screen to the server every few
  seconds so a screen answer can start sooner. Each one made the server start a paid picture read with the AI model, up to
  six a minute, even for a signed-in account with 0 answers left that could never pay for the answer. A free account that
  ran out and kept listening cost money every minute, and anyone could make throwaway accounts to do it on purpose. The
  server now checks once a minute whether the account can pay for a screen answer (answers left, bought minutes, or an
  unlimited plan) and skips the read ahead if not. The check runs in the background, so sending the screen and paying
  users' answers are not slower. If the balance cannot be read, the read ahead still runs. 3 new tests.

**Checked and fine:** interview history (only your own, by verified email), referral rewards (one claim per new account,
capped per person), unsubscribe and the hourly follow-up timer (signed tokens), Stripe portal and checkout return
addresses (fixed list, not taken from the request), listening reports (mock minutes still count), companion and presence
(your own account only), account delete request, resume save and load (your own only), screenshot hand-over (only the
person who sent it can use it), question sets and feedback (charged, refunded on failure), admin routes (owner's verified
email).

**Left as it is:** a free account that has not begun its one free Maya interview gets practice question sets and feedback
free until it begins it. Known since the 2026-11-02 audit and kept because the phone app never begins one.
