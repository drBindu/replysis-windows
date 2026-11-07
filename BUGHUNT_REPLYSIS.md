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

## Round 11, 2026-11-07: payments and webhooks (second look)

Found 3 real bugs, all on the website, fixed in uiii/frontend (not live until a website deploy).

1. **Refunding the Maya add-on cancelled the whole plan.** Adding Maya to a running Pro plan charges a separate mid-month invoice. If that charge was refunded in full (or disputed), the webhook treated it like a month of the plan and cancelled the whole Pro subscription at once, so a customer who only wanted the add-on back lost Pro too. Now a refunded mid-month charge only takes the Maya add-on off; if it paid for some other change, the plan stays and the owner is told to check by hand. Checked by scripts/check-refunds.mjs.
2. **An auto add of minutes approved through checkout could not be refunded properly.** When a bank asked the person to approve an auto add, the checkout made for it did not put the account on the payment itself, so a later refund or dispute of it could not be matched and the minutes stayed. The payment now carries the account and minutes, like every other purchase.
3. **An auto add could charge the card and add nothing.** The saved card was charged first and the minutes written after. If that write failed once, the person had paid and got no minutes, and no webhook would ever add them; the next auto add would charge again. The write is now tried three times (it can never add twice, it is keyed on the payment), and if it still fails the owner gets an email to add or refund by hand.

Checked and fine: every checkout price is chosen on the server (country from the address, packs and minutes from fixed lists); every purchase is granted once per payment; packs, India months and minutes wait for paid money; refunds of packs, minutes, India months and Practice.

Left for the owner: refunding an OLD month of a subscription that is still running cancels the current month too (the rule agreed on 2026-11-02 is "a full refund removes paid access"). A won dispute does not give access back on its own.

## Round 12, 2026-11-07: the speech engine (second pass)

Found 2 real bugs, both on the path used for languages other than English (Speechmatics). English runs on Deepgram only and is not affected. Fixed in speechmatics_engine.py; the bundled engine (engine-dist) must be rebuilt for the next Windows version, and the Mac ships the same engine.

1. **After one error from the speech service, listening never came back.** When Speechmatics ends a session with an error, the engine remembers it so the session closes. It was meant to forget it before every new connection, but that line sat inside the branch for a raw key typed into Settings, so with the normal key from our server it never ran. Every reconnect after the first error closed at once, and listening stayed dead until the app was restarted. It now forgets the error before every connection.
2. **A Speechmatics outage stopped listening for the whole session, with a wrong antivirus message.** Since 2026-11-04 the server sends a placeholder key when Speechmatics has no token but Deepgram does. A Spanish or Hindi user (Speechmatics only) then got an engine that tried to use the placeholder, failed, printed FATAL, and the app showed "your antivirus may be blocking Replysis" and stopped trying, even after Speechmatics came back. The engine now recognises the placeholder and exits with the app's normal "fetch a fresh key and start again" code, without FATAL, so the app keeps retrying on its own pace.

Also repaired: tests/engine/test_streaming_options.py had been failing since the Flux fallback flag was added (the test did not define it). New test: tests/engine/test_speech_key_and_session_error.py.

Checked and fine: the transcript kept across Deepgram reconnects and the replayed audio, the muted Flux refresh (the app always starts a new question with reset.flag, so clearing on refresh loses nothing), atomic latest.txt writes, the bounded memory of the turn map, chunk levels and replay log over a long session, the paced idle loop, keepalives on nova-3 and pings on Flux.

Not changed: a Flux turn left open by a very quick Space press could in theory carry the end of the previous question into the next one in Manual; this needs the live service to confirm, so it is only noted. The three older failures in test_line_resilience.py are unchanged from before this round.

## Round 13, 2026-11-07: running the website and server fixes, not just reading them

No new bugs. This round ran every payment fix since the 2026-11-04 deploy end to end on this laptop: the website against local copies of the database and sign-in (emulators), Stripe in test mode only, email switched off. A probe confirmed nothing reached the live database before any test ran.

Results: 58 of 58 scenarios pass.
- 35 earlier webhook scenarios (plans, renewals, India months, packs, Practice, refunds, disputes, two subscriptions). One old test was out of date: since 2026-11-04 only a failed renewal holds off the monthly refill, and the test sent a failure with no reason. The test now checks both: a failed mid-month charge does not hold the refill, a failed renewal does.
- 6 signed-in checks (checkout, stored customer reused, second subscription refused, another person's account refused, Maya's replies and voice refused for a spent free account).
- 17 new checks for round 11, on real Stripe test payments:
  - Refunding the mid-month Maya add-on charge keeps Pro running and takes only Maya off; the account stays Pro without Maya; a repeat refund event changes nothing.
  - Refunding a mid-month Pro to Max change charge keeps the plan and its items, and the account is untouched.
  - Refunding a first month still cancels the plan, as agreed on 2026-11-02.
  - An auto add of minutes charges once and lands its minutes; pressing again inside the lock neither charges nor adds; the payment carries the account and minutes; its refund takes the minutes back.
  - A card that needs bank approval gets a checkout link and no minutes yet; a payment carrying that checkout's details is refunded correctly.

Not run end to end: the three tries when writing auto add minutes fails (a database write cannot be made to fail on demand here). Read instead: each try checks the payment's receipt inside the same write, so a retry can never add twice. Completing a hosted Stripe checkout page cannot be done from a script, so test D used a payment with the same details.

Also checked: website checks (safe path, resume text, refunds, sync, type check) and server tests (168, none failed) all pass. Commits since the deploy were read together: the two server changes to InterviewController touch different parts (the charge thread and the screen read ahead) and do not interfere; resume escaping cannot double escape (resume fields hold no formatting); the website and server rules for free mock interview steps still match branch for branch.

Ready for the owner's deploy: website (4 commits) and server (2 commits) since 2026-11-04.

## Screen read model test, 2026-11-07 (owner approved; nothing live changed)

What was tested: the model that reads the screen now (gemini-3.5-flash-lite), its backup (gemini-3.1-flash-lite), Google's moving "flash lite latest" name, and the faster looking options the existing keys can reach (Qwen 3.8 27B on Cerebras and on Groq). The two newer Gemini Flash models answered in about 5 s on the first try, and OpenAI refused every call ("account is not active"), so those were dropped early.

How: 12 made-up screens, no customer data, drawn with headless Chrome at 1920 x 1080: a LeetCode problem, a SQL task, a system design brief, an aptitude question, a Teams chat question, a busy desktop with a sticky note, a Java compile error, a problem cut off at the bottom, a settings page with nothing asked, a failing Python test, a Python output quiz and a Zoom shared coding task. Each model got the app's exact F8 screen prompt and the same request the server sends, 3 times per screen, run from inside the website container so no key left the server. 180 calls in all.

Time to first words (straight to the model, so lower than what customers see end to end):

| Model | Typical | 9 in 10 within | Slowest | Right answers |
|---|---|---|---|---|
| gemini-3.5-flash-lite (now) | 0.94 s | 1.58 s | 2.17 s | 30 of 36 |
| gemini-3.1-flash-lite (now the backup) | 0.87 s | 1.15 s | 1.37 s | 32 of 36 |
| gemini flash lite latest | 0.84 s | 1.33 s | 1.85 s | 30 of 36 |
| Qwen 3.8 27B on Cerebras | 1.10 s | 2.89 s | 3.16 s | 34 of 34, but 2 blank answers |
| Qwen 3.8 27B on Groq | 0.42 s | 0.60 s | 1.30 s | 28 of 36 calls refused (free plan limit) |

What stood out:
- The current model answered the Python quiz wrong twice in three ("16", then corrected itself in the middle of the answer). The backup got it right every time and was also the steadiest.
- Every Gemini model solved the cut-off problem from what it could see instead of asking to scroll for the missing constraints. Only Qwen asked.
- Qwen on Cerebras thinks before it writes: twice it spent its whole allowance thinking and returned nothing after 3.6 s, which would be a blank answer in front of an interviewer. Not safe as it is.
- Groq is fastest but its free plan refused most calls, so it cannot carry real traffic.
- "flash lite latest" is a moving name Google can repoint at any time, so it is not a safe fixed choice.

Recommendation: swap the two Gemini models, so gemini-3.1-flash-lite reads the screen first and gemini-3.5-flash-lite becomes the backup. It was a little faster on typical screens, much steadier on slow ones (slowest 1.4 s against 2.2 s) and more often right. It is two lines in InterviewController.java (VISION_MODEL_GEMINI and SECOND_CHOICE_MODEL) and needs the owner's yes plus a server deploy. Not changed.

Also learned: the model itself takes about 0.9 s here, but customers wait about 2.8 s for a screen answer (round 7). Most of the difference is outside the model (bigger real screenshots, several images when scrolled, the two step path for coding screens), which is the next place to look for speed.

Cost of the test: about 290,000 tokens in and 90,000 out across all models, roughly 20 to 30 US cents.

## Round 14, 2026-11-07: long sessions and stability

**Fixed: Auto got slower the longer the interview went on.** In Auto the transcript is never cleared between answers, so it
holds the whole interview. Every 40 ms, on the same thread that draws the window and handles Space, the app matched that
whole transcript word by word against the part already answered. Measured: under 1 ms a poll for the first few minutes,
about 4 ms after an hour of talk (a tenth of that thread's time) and about 8 ms after two hours (a fifth). That is time
taken from typing out the transcript and reacting to Space and Auto, exactly when an answer is needed.

Now the full match runs once after each answer. Later polls only check that the transcript still starts with the same
answered words and take what follows, which costs about 0.07 ms even two hours in. If anything about the start changes
(the recogniser rewrites the last answered words, or a new transcript starts), the full match runs again as before, so the
result is always exactly what it was. A test checks the cached result against the full match across twelve edge cases and
that a poll two hours in stays under 1 ms.

**Checked and fine:**
- Timers: every timer is created once or stopped before it is replaced; none pile up across New session or reconnects.
- The debug log on disk rotates at 20 MB, and the on screen log keeps 200 lines and only redraws while open.
- The transcript typing hooks into screen frames only while it is typing, and unhooks after.
- Crashes: async handlers fall back to the app wide handlers, which log and keep the app running.
- The power (sleep and wake) handler is added once and removed on close.
- Saving each answer to the session file happens in the background queue, never on the answer path.

**Not changed, noted:**
- The speech engine also keeps the whole Auto transcript and rewrites it to disk on every update, up to about 100 KB several
  times a second two hours in. That is disk writes, not answer time; changing it needs an engine rebuild, so it is left for
  a later engine round.
- The answer window can only be dragged within the main screen's work area, so it cannot be moved onto a second monitor.
  That matches the Mac and looks deliberate.

## Round 15, 2026-11-07: screen answer speed

Where the wait goes, from the server's own timing lines and the web server log (read only):

- Every screen answer since the Nov 4 deploy (3 of them, all F8 with the picture sent in the question) went the same way: the charge took about 0.1 s, then a full screen read took 1.4 s, 2.6 s and 3.0 s, and that read only said "not a coding screen". The read was thrown away and the picture model was then asked for the answer from scratch, which takes about another second before the first words.
- Why: the server decides whether to do that read by looking for words like "code", "fix" or "error" in the app's whole instruction text, and the app's instructions always contain them. So every screen, coding or not, waited for a read first.
- The read made ahead of the question (while listening) would hide this, but it only helps when the screen was sent ahead. F8 sends the picture with the question, so it never helps there.
- The app side was checked and is already tuned on purpose (picture fitted to a set size, squeezed under a byte limit before sending), so nothing there was changed.

Fixed (backend):

1. The picture answer now starts at the same moment as the screen read, not after it. If the read finds a coding problem, the coding path answers as before and the early picture answer is closed so the model stops. If not, the picture answer is already on its way. Expected: about one second off every screen answer on a non-coding screen (from read plus answer, to about the read alone). Coding screens are as fast as before. A read finished ahead of the question still skips this. Answers, refusal hold, billing and refunds are unchanged. Cost: on coding screens one extra short picture call is started and closed, a fraction of a cent.
   Tests: EarlyVisionTests (when it starts, and that an unused one is always closed, even if it arrives late). 172 backend tests pass.

Left for later, on purpose: deciding "coding or not" from the interviewer's words instead of the whole instruction text would skip the read entirely on ordinary screens, but it changes which path answers, so it needs a side by side check first. Needs the owner: a server deploy.

## Round 16, 2026-11-07: a new user's first run on any Windows

Checked: first launch with no settings, the engine and app agreeing on where the transcript lives, non English
Windows (number and date formats, calendars), user folders with accents or Indian scripts, microphone privacy
switches for the website copy and the Store copy, an antivirus removing the speech engine, and the first Start.

Fixed:
1. **An antivirus that removed the speech engine made Replysis tell a customer to install Python.** PyInstaller
   programs are often quarantined. With speechmatics_engine.exe gone, the app fell back to the developer path and
   said "Install Python 3.11 or newer, then run: pip install -r requirements.txt". Now an installed copy whose
   engine is missing says part of Replysis is missing, the antivirus may have removed it, allow Replysis there and
   install it again from replysis.com. It is retried like a failed start and said once, after 3 tries.
2. **The Store copy never asked Windows for the microphone.** Its package listed only internet and full trust, so
   Windows shows no microphone switch for it and can keep the microphone from it. The package now declares the
   microphone. Needs the next Store package to take effect.
3. **A Store user who switched Replysis off in microphone privacy was told to restart the computer.** The check
   only looked at the desktop apps switch, which does not apply to a Store app. It now reads the Store copy's own
   switch, so they are told to allow the microphone.
4. **On a Thai Windows the app sent lastActive 543 years ahead and showed past sessions in 1483.** A custom date
   format still follows the computer's calendar (Buddhist on Thai, Hijri on Saudi Arabian Windows). The time sent to
   the server and the session date read back now use a fixed calendar.
5. Quality: the app now tells the engine which folder to write the transcript to (APP_DATA_DIR), instead of each
   working it out its own way.

Checked and fine: decimal numbers between engine and app are read with a fixed format; the engine opens every text
file as UTF-8, so accented or Indian script user folders work; first launch without settings uses safe defaults.

Tests: 26 new checks in FirstRunTests (removed engine, six cultures incl. Thai and Saudi, Store and website
microphone switches, the manifest).

## Round 17, 2026-11-07: phone only, phone commands, admin and alert emails

Fixed:
1. Two customers simply running out of answers could email the owner "apps are reporting errors". Every banner the app shows was also sent to admin under its own title, and the alert rule only recognised a few words ("nocredits", "free answers", "trial"). A paid customer out of answers sent "No answers left this month", and every Space press with an empty balance sent "You have 0 answers left"; neither matched, so two such customers within ten minutes made the outage email. The month's limit and "no microphone" did the same. Now the app reports a problem once, by its kind, and marks a customer's own state (out of answers, this month's limit, no microphone) as minor. They still show in the admin Live panel. The server also recognises the titles older Windows versions send, so this stops as soon as the server is deployed, before any app update. Real problems (speech service busy, still connecting, reconnecting, sign in expired) still count.
2. With "Answers on my phone only", every Replysis window is hidden and it is not in the taskbar. If Ctrl+Alt+H or Ctrl+Alt+R did not work (forgotten, or another program holding the keys), opening Replysis again only said "already running, check your system tray or taskbar", where it was not, and the only way back was Task Manager. Opening Replysis again now brings the running window back. An older copy that cannot do this still gets the old message.

Checked and fine: the phone's Stop and Answer reach only the same signed in account's laptop and expire after 30 seconds; the phone stream only ever shows that account's own answers, guests are never relayed, and the phone's own answers are not repeated to it; a dropped answer still marks the phone's copy finished; old turns go after two hours; the laptop stops sending what it hears when no phone is open.

Skipped: two laptops on one account both pick up a phone command, whichever reports first gets it. Rare, and changing it needs the phone app too.

Checks: the app and all test projects build with 0 errors; CleanerTests all passed (12 new checks); server tests 173 run, 0 failed.
Needs the owner: the server part needs a deploy; the app part needs the next Windows version.

## Round 18, 2026-11-07: local security and privacy of the Windows app

**Fixed: another account on the same PC could listen to the microphone and the interviewer.**
The app hands the microphone and the computer's sound to the speech engine through two named pipes. They were made
with Windows' default security, which lets Everyone and Anonymous open a pipe for reading (checked on this laptop:
"Everyone Read", "ANONYMOUS LOGON Read"). Pipe names can be listed by any program. On a shared PC, with another
account signed in or a remote desktop session, a program of that account could connect first and receive the live
sound; our engine would then quietly fall back to its old capture. The pipes now allow this Windows user only.
The engine runs as the same user, so nothing changes for it, and no time is added. Test: section 29 of
CleanerTests (access list is this user only, nothing for Everyone or Anonymous, same user still reads).

**Checked and fine:**
- Session, settings, resumes, job context and the speech token are encrypted for this Windows user.
- latest.txt and reset.flag sit in the user's own app data folder and latest.txt is deleted on close.
- The engine gets its keys through environment variables, never the command line, and its arguments take no free
  user text except the microphone name, with quotes removed.
- The bring back signal from round 17 lives in this sign-in session only and carries no data.
- Google sign in uses PKCE and checks the state value.
- The debug log and problem reports hold lengths, not what was said, read on screen or typed.
- Screen captures and answers written to the temp folder exist in developer builds only.

**Skipped:** the speech vocabulary file holds words from the resume in plain text in the user's own folder.
Only this user (and administrators) can read it, the same as the encrypted files' location, so it is not a hole.

## Round 19, 2026-11-07: the website's interview flows (browser real interview and Maya mock interview)

**Fixed (website, not live until a website deploy):**

1. **The microphone could stay on after listening was stopped.** In the browser real interview, opening the
   microphone can take seconds (the permission prompt, a Bluetooth headset waking up). If Space was pressed again in
   that time, the microphone opened after listening had already stopped: the browser kept its red recording dot, the
   screen said "Listening…", and nothing was sent anywhere until the tab was closed. The listening client now checks
   after each step that it is still the live one and closes a late microphone at once.
2. **A second Space press, or a held Space key, started a second listener.** Starting reads the sign in token first,
   and a press in that short gap started another listener. Two microphones fed the same transcript, so words showed
   twice, listening minutes were counted twice against the customer's fair use, and the first listener could not be
   stopped because nothing pointed at it any more. A held Space key repeats about 30 times a second, so this was easy
   to hit. Now only the first press counts, a second start is refused, and an error from a replaced listener cannot
   end the live one.
3. **One interview could be saved twice in the history.** The first save creates the interview and the next ones
   update it, but a second save sent before the first reply came back (End session pressed right after an answer)
   had no id yet and created a second copy. Saves now run one at a time.
4. **Copy rule:** two ready made spoken replies had a dash ("great to be here - really looking forward", "exciting
   challenge - I've been following"). Now full stops.

Test: `scripts/check-listening-start.mjs` (fails on the old listening client, passes now): a microphone opened after
stop is closed and nothing says Listening, a normal start still listens, the hook ignores a held Space and a second
start, saves wait for the one before, and the spoken replies have no dash.

**Checked and fine:** the answer request never trusts the email or model sent by the browser (server verified email,
model allowlist and plan clamp); session saves and reads check the signed in owner; the Maya face session checks the
plan, the free interview, the daily and lifetime join caps and the month's count; the free interview is claimed once
in a transaction; listening minutes use a fresh sign in token and are sent with keepalive when the tab closes.

**Skipped:** the ready made replies to a hello or small talk are not auto saved until the next AI answer or End
session; both save the whole history, so nothing is lost unless the tab is closed right after a hello.

## Round 20, 2026-11-07: the Windows app's screens and keyboard shortcuts

Fixed (Windows, local only, not released):
1. **Spaces vanished from the pay expectation box, and a pause before one started listening.** The app decides "the candidate is typing in one of our fields" from a fixed list of four boxes, and the pay box was not on it. So in that box every space was swallowed (typing "20 to 25 lakhs" gave "20to25lakhs"), and a space after a pause of a second also started listening and jumped from setup to the interview. Now any editable field or password box in any Replysis window counts, and read-only boxes (the answer, the code panel) still do not block Space. The Auto branches of a Space press (resume a paused Auto, answer now) now also respect typing in a field; they ran before that check.
2. **Indenting code with spaces sent answers.** With "Space works in any app" switched on, indenting a line in a code editor after a pause is several spaces in a row. Each one toggled listening: start, answer (charged), start, answer. Now a Space within a second of the previous Space counts as typing, like a Space right after a letter. The first Space after a pause still toggles, and Ctrl+Alt+Space always does.
3. **Code lost its indentation in the compact window when the answer had bullets.** With a MORE TO SAY list, every line of the answer was trimmed, so Python code read from the compact window had no indents, which is a different program. Lines inside a code block are now kept as written; only the prose and bullets are tidied.

Checked and fine: F7, F8 and F9 debounce and pass-through, Ctrl+Alt shortcuts, injected keys from dictation tools ignored, the hook never throws, the main window's code panel and complexity line, the compact window's scroll to the start of a new answer.

Tests: `tests/CleanerTests/UiHotkeyTests.cs` (16 checks). Ships with the next Windows version.

## Round 21, 2026-11-07: resume and AI tools

Found 2 real bugs, both in the website's browser interview (/real-interview). Fixed in website commit c3157600, not live until a website deploy.

1. Every space in the resume and job description was sent to the AI as a hyphen. The resume cleaner had a pattern meant for long dashes that had become "[- - ]", which matches a plain space, so "Senior Engineer at Acme Corp" reached the model as "Senior-Engineer-at-Acme-Corp". It also dropped every letter outside plain English, so an accented name like Jose with an accent lost that letter. This has been there since the first commit and is live today. Now spaces and letters stay; only long dashes, smart quotes and hidden characters are made plain.
2. The answer route cut every prompt turn at 6,000 characters. The rules part of the system prompt is about 7,500 characters on its own, so the coding interview and system design instructions and the chosen answer style, which sit at the end, were always cut off, and a resume over about 5,500 characters was cut part way. The system turn may now be up to 40,000 characters; other turns keep their 6,000 cap. The prompt is now as long as it was designed to be, about 2,000 to 3,000 more input words for the model, which is a few tens of milliseconds at most on these models.

Checked and fine: resume upload limits and scanned PDF message, the Windows app's resume size caps and what it sends with each answer (the resume rides inside the prompt, well under the server's limits), refunds on failed analysis and tailoring, and that the costs on /pricing (analysis 1 answer, tailor 4, question set 1, feedback 1, rewrite 1) match what the website and server charge.

Skipped: resume import from a file is free and calls the AI, limited to 6 a minute per account. That is deliberate (it is how someone starts), noted here only.

Checks: npx tsc --noEmit clean, check-sync ALL IN SYNC, the new scripts/check-prompt-text.mjs passes, and the listening, resume text, safe path and refunds checks pass. Not run in a browser and no live model calls.
