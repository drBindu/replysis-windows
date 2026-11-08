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

## Round 22, 2026-11-07: checking all the Windows changes together

All 30 Windows commits since 1.0.52 (89a202f) were read as one change, to catch fixes that clash with each other.

Fixed:
1. The quick second Space rule from round 20 also applied inside Replysis itself. There a quick second press is meant: stop listening, then a moment later press again to interrupt the answer. Inside Replysis that second press was ignored for a second. The rule now applies only when another app is in front (indenting code in an editor), which is the case round 20 was for. Replysis's own text boxes were already safe.
2. Three information banners still counted as app errors toward the "apps are reporting errors" email: "Microphone found", "Nothing heard yet" (already reported once by its own kind) and "Your minutes are almost over" (a bank asking to approve minutes). Two of those within ten minutes, on two computers, was enough for a false outage email. They are no longer reported as errors.
3. Test repair: tests/engine/test_weak_line_audio.py had been failing since 1.0.41 because it did not load one engine flag. It passes now. No engine code changed.

Checked and fine together: Space and Auto rules (typing in a field, Auto paused after Finish and resumed by Space, the mic, Start or New session, answer now in Auto at most once a second), problem report kinds against the server's rule, the update notice hold and the stuck notice fix, the second launch bring-back, and no new waits on the answer path.

Still failing, same as before these rounds: 3 older tests in test_line_resilience.py.

Checks: the app and all 5 test projects build with 0 errors; CleanerTests all passed (5 new checks); every engine test file passes except those 3 older ones.

What the next Windows version contains (all local, nothing released):
- Listening: the start of a question is kept when the speech connection drops; no repeated words after a reconnect; faster recovery after sleep; listening time is no longer lost when a report fails; a single speech service error no longer stops listening for other languages; an antivirus or Store microphone block gets clear advice.
- Answers: no sign out on a slow refresh; a 20 second limit when no words arrive; Thinking never sticks; out of answers shows the upgrade button for trials; no false outage emails from customers running out, updates failing, wrong passwords or information banners.
- Auto: no restart after Finish; New session resumes it; no slowdown in long interviews.
- Screen share: the black box stays on during screen reads; the right monitor is read with two screens.
- Settings: Save no longer resets other settings; an unplugged microphone is remembered.
- Keyboard: spaces work in every text box; indenting code in another app no longer sends answers; code keeps its indentation in the small answer window.
- Trial: shown as Free trial with correct numbers, never Pro.
- Updates: no stuck update notice; no update notice during an interview.
- Safety: audio passed to the speech engine is readable by this Windows user only; opening Replysis again brings back a hidden window.
- Needs before release: engine-dist rebuild, one try on a black box laptop, a second monitor, a Store install microphone switch, and the owner's yes.

## Round 23 (2026-11-07): website and server checked together before the next deploy

No regressions found. Nothing changed in the website or server this round.

What was read as one change: every website commit (6) and server commit (4) since the Nov 4 deploy, rounds 1 to 21.
- Fixes that touch the same code do not fight: the payment refund rules, the minutes auto add, the trial ended email and the sign-in redirect each stand alone.
- Phone and Mac apps: the only shared route that changed is the website answer route, and only the browser interview's message list uses the new prompt limits. Maya, her voice, practice questions and listening tokens are unchanged. The server's new alert rule only makes more customer states count as minor, so Mac and iPhone reports raise fewer false alerts, never more.
- Speed: the browser interview now sends the whole system prompt instead of cutting it at 6,000 characters. For a normal resume that is about 1,500 more words of reading for the model, a few hundredths of a second at the speed these models read. A pasted resume of the full 30,000 characters could add more, for that one person only; left as is because cutting it is what lost the coding and answer style instructions. Windows answers are not touched.
- Screen answers: the early picture answer only starts after the answer is paid for, guests never reach the new read ahead balance check, and an unused early answer is always closed.
- Billing: no change to what anything costs. The free mock interview steps rule is still the same on the website and the server (neither file changed).

Checks: website type check clean; all six website checks pass (listening start, prompt text, refunds, resume text, safe path, sync: ALL IN SYNC); a full website production build passes (built from a separate copy so the running dev server was not touched); server tests 173 run, 0 failed.

Waiting in the next website and server deploy, in plain words:
- Sign in: the after sign-in link can no longer send someone to another website.
- Emails: an ended free trial gets "Your free trial has ended", not "you used all your answers"; no "Hi User".
- Resume PDF keeps text written in angle brackets; no middle dots in two places.
- Browser interview: the microphone never stays on after stop; a held or double Space cannot start two listeners; an interview is saved once; spaces and accented letters in the resume reach the AI; the AI gets its whole instructions.
- Payments: refunding the Maya add-on takes only Maya off; bank approved minute top ups can be refunded; a charged top up always adds its minutes or emails the owner.
- Server: no paid screen read ahead for accounts with nothing left; screen answers about 1 s faster on screens without code; voice answer timings record which app asked; out of answers and no microphone from older Windows apps no longer trigger outage emails.

Safe to deploy: yes, from these checks. Still untried: a real browser on the browser interview fixes, and the screen answer change against the live AI.

## Round 24, 2026-11-07: two writers on one account record (website and server)

Looked for lost updates and double refills where the website and the Java server both change the same account record (answers, used count, bought packs, minutes, refill date, plan, trial end, Practice).

Code: every balance change on both sides already runs inside a transaction (charges, refunds, monthly refills, listening minutes, trial start, trial and India month end, webhook grants), so two of them at once are put in order by the database and neither is lost. The trial end re-reads the account and skips it if a month was bought in between. No code race found.

The real hole was in the database rules, which decide what a signed-in browser may write directly. The live rules are an old copy from 2026-06-22, not the stricter file on disk:
1. Any new account could create its own record before the server did, with any extra field it liked. Example: sign up through Google's sign-in API, then write your own record with minutesBalance 99999 and mockPlan true. The server then treats every live answer as paid for by bought minutes (no answers taken) and opens Maya as if Practice were bought. Unlimited free answers and Maya for anyone who tries.
2. Any existing account could set its own used count back to 0. A customer with a bought pack could keep resetting it so the pack is never spent and comes back in full at every monthly refill.
Proved on the Firestore emulator with the live rules (both allowed) and with the fixed rules (both refused, while the apps' own presence writes still work).

Fix: `uiii/firestore.rules` now refuses every client create; the server already makes every record itself (website sign-in and the server's first balance check, both with the 1 day trial). The update rule on disk already keeps credits and creditsUsed server only. New check `scripts/check-rules.mjs` in the website repo fails if a client could ever write a balance, plan or minutes field again.

Production, read only: 52 accounts. 7 free accounts hold 100 answers with no trial fields, which matches the old Free plan before trials, and one has 20 interview minutes. Nothing here proves anyone used the hole, but it cannot be ruled out from the data.

Needs the owner: the rules only change when deployed, with `firebase deploy --only firestore:rules` from the uiii folder. It touches no app and no website code; the website, Windows and Mac apps only write activity fields, which stay allowed. Note `uiii/firestore.rules` is not in any git repo, so it has no history.

## Round 25, 2026-11-07: cloud settings (Firebase, keys, CORS, headers), read only

Everything was read without changing anything live. One real risk found, which only the owner can switch off in the console.

**Risk: phone number sign in is switched on in Firebase, but no Replysis app uses it.**
- Firebase Authentication has the "Phone" provider enabled. No website, Windows, Mac or phone code calls it (searched for signInWithPhoneNumber, PhoneAuthProvider and RecaptchaVerifier: none).
- An enabled, unused SMS sign in is the usual target of "SMS pumping": a script asks Firebase to text codes to expensive numbers, and each text is billed to the project on the Blaze plan. It brings no customer benefit to keep it on.
- I only sent one test request with an invalid number, so no text went out, and I did not try to get past Google's own bot checks. So this is a risk to close, not a proven attack.
- **Owner steps:** console.firebase.google.com, project copilotx-ai, Authentication, Sign-in method, Phone, switch Enable off, Save. Nothing in the apps changes.

**Checked and fine:**
- **Website bundle:** the live site's browser files contain no Stripe secret or webhook keys, no private keys, and no Deepgram, Gemini, Cerebras, Groq, OpenAI or Resend keys. The only key is the public Firebase web key, which is meant to be public.
- **That public key:** it is limited to certain Google services. A test from a made-up site was refused for Gemini and Translate ("API_KEY_SERVICE_BLOCKED"), so nobody can spend the AI bill with it. Maps is not switched on for the project. The same key is in the Windows app.
- **Firebase Auth:** email enumeration protection is on, email and Google sign in are on, Apple is off. Authorized domains are localhost, the two Firebase domains, replysis.com, www.replysis.com, coopilotxai.com, verchor.com and www.verchor.com; all the custom ones still point at our own server.
- **Storage and Realtime Database:** their management APIs are not even switched on for the project, so neither is in use.
- **Database rules:** the fixed rules from round 24 were put live by the main session (rollback ruleset recorded in memory).
- **Server CORS:** answers only replysis.com, www.replysis.com, the old coopilotxai.com and localhost, with credentials off. A request from another site is refused (403). Sign in is by a token header, not cookies, so the localhost entries cannot be used to read anyone's data.
- **Website headers:** HSTS with preload, a content security policy, X-Frame-Options, nosniff, a referrer policy and a permissions policy are all present. The policy still allows inline scripts, already noted on 2026-11-02.

**Low, owner's choice:**
- `uiii/firestore.rules` is not in any git repository, so a future change has no history and could be overwritten by an old copy, which is how the live rules fell behind. Moving it into the frontend repo would fix that.
- When the old domains coopilotxai.com and verchor.com are let go, remove them from Firebase authorized domains and from CorsConfig.java first, so a new owner of the name cannot use them.

No code changed this round.

## Round 26: older Windows versions against the current and next server (2026-11-07)

Who runs what (Firestore, read only): Windows 1.0.52 on 2 accounts, 1.0.51 on 3, 1.0.50 on 4, and one each on 1.0.46, 1.0.40, 1.0.39 and 1.0.34. Only 1.0.51 and 1.0.52 opened the app in the last 14 days (4 accounts). Phones: iPhone 1.0.0 on 3, Android 1.0.0 on 1.

Checked and fine:
- The new database rules (live today): every Windows version back to 1.0.34 writes only lastActive and lastLogin, to a record that already exists. Both are still allowed.
- The server waiting to deploy (local HEAD): the screen read ahead skip, the screen answer started beside the screen read, and the app labels change nothing an old app sees in its replies. The listening report still accepts up to 120 minutes, more than the next app ever sends (an hour at most).
- The next Windows version against today's live server: everything it reads (the trial flag, the out of answers reply, the placeholder speech key) is already live, so the app can ship before the server.

Fixed (server, waiting for deploy):
1. Older apps could still send the owner a false "apps are reporting errors" email. 1.0.52 and older report a mistyped password, a failed update download and three information banners ("Microphone found", "Nothing heard yet", "Your minutes are almost over") as real errors. The next app marks them minor, but customers stay on old versions for weeks. The server now recognises those exact old reports. A disabled account, a Google sign-in failure and a failed install still alert. 2 new tests.

Left as it is:
- On 1.0.52 and older, when Speechmatics is down and the person listens in a language other than English, the server's placeholder key makes the old engine stop with an antivirus message until the app is restarted. Before 2026-11-04 the server refused the whole request instead, which stopped English listening too. English is unaffected. The next Windows version fixes it in the engine (round 12).

Deploy order: either order works. The server first is slightly better, because it stops the old apps' false alerts at once.

## Round 27, 2026-11-07: customer emails and scheduled jobs

Every customer email was traced: welcome (server, a minute after signup), follow-up (website timer, 6 to 72 hours after
signup), upgrade emails "Keep practising with Maya" and "You have used your answers" or "Your free trial has ended"
(website timer), trial reminder 4 hours before the end and India month renew reminder 3 days before (server), plus the
owner's own alerts. Production was read only: 52 accounts, nobody has unsubscribed yet, 6 test accounts, and the sent log
shows a trial reminder followed 35 minutes later by a follow-up, and another by an out of answers email 4.5 hours later.

Fixed:
1. Two emails could go to one person in the same minute. The Maya nudge and the out of answers email are separate kinds,
   so someone who had their free Maya interview and used their answers got both in one run. Now one upgrade email per
   person per run (out of answers first; the Maya one comes on a later day), and no follow-up or upgrade email goes within
   12 hours of any other email from us, including the server's trial reminder. The welcome is left out of that, so the
   follow-up still arrives about 6 hours after signup while the 1 day trial runs. Website 6891a425.
2. A paying Pro customer without the Maya add-on got "Keep practising with Maya" selling the Practice plan. Paying
   customers no longer get that nudge. Website 6891a425.
3. A failed send was lost for good. Every email is claimed first so two server copies never both send it, but the claim
   stayed when the mail service failed, so that person never got their welcome, trial reminder, follow-up or upgrade
   email. The claim is now given back on a failed send and the next look tries again. Server 3a01b86, website 6891a425.
4. The trial reminder ("choose a plan") ignored unsubscribe and went to the owner's test accounts. It now skips both,
   like the website's emails. Server 3a01b86.

Checked and fine: the two server copies never both send (every email is claimed in _alerts first); the trial timings
line up (welcome at signup, follow-up at 6 hours, reminder at about 20 hours, ended email after that); times are stored
in UTC and the India renew date is shown in India time; batch limits of 100 per run are far above today's numbers; no
email text has dashes, middle dots, plan counts or blames the network.

Left for the owner: the welcome and trial reminder (sent by the server) have no unsubscribe line, unlike the website's
emails. Adding one needs the website's unsubscribe secret on the server. Low risk today (nobody has unsubscribed), but
worth doing before sending more marketing from the server.

Checks: new scripts/check-email-plan.mjs passes, npx tsc --noEmit clean, check-sync ALL IN SYNC, server tests 177 run,
0 failed (2 new). No email was sent. Needs a website and server deploy.

## Round 28: release readiness of the next Windows version (2026-11-07)

Nothing broke. Nothing was released, pushed or dispatched.

What I checked:
- The bundled speech engine was rebuilt from the current source (tools\build-engine.ps1, the same script the release workflow runs). The built engine says it came from source 704c72cbb381, which is exactly the committed speechmatics_engine.py. engine-dist is not in git, so nothing large was committed.
- The built engine, run on its own with no windows: starts and says WARMUP OK, ignores arguments it does not know, stops with exit 1 and FATAL when it has no key, and on the server's placeholder key stops with exit 2 and no FATAL (so the app fetches a fresh key instead of showing the antivirus message). Its output reads correctly as UTF-8.
- The reconnect and reset behaviour can only be tested on the source file, because the built engine always talks to the real speech services. Those source tests pass. test_line_resilience.py still has its 3 old failures, the same ones it had before any of these rounds.
- The app built in Release the way the release workflow builds it, with the engine inside it. All 3 test projects build with 0 errors, and CleanerTests all passed.
- The release workflow's version stamp works on the project file (checked on a copy, the real file is untouched), and packing the installer locally, unsigned, produced Setup.exe, the portable zip and the update package. Those were deleted afterwards. The Store package manifest is valid and asks for the microphone.

What the next Windows version contains, in plain words:
- Listening keeps the whole question after a drop, does not repeat words after a reconnect, and recovers faster after sleep.
- Languages other than English recover from a speech service error and no longer show a wrong antivirus message during an outage.
- No sign-out when a sign-in refresh is slow. Answers that never start stop after 20 seconds, and Thinking never sticks.
- A free trial shows as Free trial with the right numbers and an upgrade button when it runs out.
- Auto does not restart after Finish, New session resumes it, and it no longer slows down in long interviews.
- Screen share hiding holds during screen reads, and the right monitor is read on two screens.
- Settings Save no longer resets other settings, and an unplugged microphone is remembered.
- Spaces work in every text box, indenting code in another app no longer sends paid answers, and code keeps its indentation in the small answer window.
- Update notices never stick and never show during an interview.
- No false "apps are reporting errors" emails from customers running out, failed updates, wrong passwords or information banners.
- On a shared PC, another Windows account can no longer listen to the audio. Opening Replysis again brings back a hidden window.
- Clear advice when antivirus removes the engine, the Store copy asks Windows for the microphone, and Thai Windows dates are right.

Checks still needed on real laptops before release:
1. A laptop where Replysis hides as a black box: share the screen, press F8, check the answers never show in the share.
2. A laptop with a second monitor: put the interview on one screen and Replysis on the other, press F8, check the right screen is read.
3. A Store install: switch Replysis's microphone off and on in Windows Settings, Privacy, Microphone, and check the app's message each time.
4. Swathi's laptop: a short real interview, then paste the debug log.

Steps to release once the owner says yes:
1. Push windowsNative main.
2. Delete any old draft, then dispatch release-windows.yml with version 1.0.53, the notes, and publish false.
3. Run the verify script and the ReplysisSmoke scheduled task on the draft.
4. Publish the draft (draft false, make latest), then change app/lib/downloads.ts on the website to 1.0.53 and deploy the website.
5. Dispatch build-windows-msix.yml with version 1.0.53 and upload the Store package in Partner Center.
6. Tell Mac Claude in CHANNEL.md; MAC_CATCHUP.md already lists every change.

## Round 29, 2026-11-07: the owner's admin panel numbers

Production was read only, counts only: 52 accounts, 6 marked as yours or friends, 1 stranger paying (an India Pro month paid once), no trials running, 2 India month receipts and 1 minutes receipt.

Fixed (website, local, needs a deploy):
1. Monthly revenue counted the India month customer twice. It priced them as a $34.99 Pro subscription and then added the rupees they paid on top. It also added every rupee sale ever made, so the "monthly" figure grew forever. Now subscriptions are priced at the dollar price, and only rupee sales of the last 30 days are added.
2. Conversion and Free accounts used every account, including your own 3 Max and 1 Pro test accounts. Conversion showed about 9.6% when it is about 2%. Both now use real users and real payers, like the Real users card.
3. India sales: plan_passes also holds dollar answer pack receipts and refund receipts. A dollar pack was added as rupees and a refund was counted as a purchase and a buyer. Only rupee receipts with an amount count now; refunds that took something back are shown on their own ("N refunds taken back").
4. A friend's account marked as yours while still on the trial was taken off the paying count twice, showing one paying customer fewer.
5. Giving a trial account a plan by hand in admin kept it marked as a trial, so the trial job put it back to free a day later and the panel counted it as a trial. A plan set by hand on a trial now stops the trial (planSource "admin"); the audit row records the old planSource.

Checked and fine: every admin action is owner only (verified email must match ADMIN_EMAIL), each runs in a transaction with its admin_audit row, and the user id is checked; delete refuses your own account. The "email everyone" list skips unsubscribed people. Latency uses latencyMs (first words). The backend /admin-api/stats is not used by the panel. Dismissed deletion requests are not shown as open (the panel does not list deletion requests at all).

Test: scripts/check-admin-metrics.mjs. Type check clean, check-sync ALL IN SYNC, all script checks pass.

## Round 30, 2026-11-07: the hidden fair use guard and cost abuse

What was checked: listening allowances per plan (trial 120 minutes, guest 10, Pro, Max 1,800, Practice, India), the 402 "monthly limit" answer on Windows and the website, how listening is metered, speech token lifetime and reuse, where the speech keys live, and the screen, Maya and voice limits from earlier rounds.

1. **Speech can be used without being metered (cost hole, not fixed in code, now watched).** Listening minutes are counted from what the apps report. A speech token from /api/v1/stt/key lasts an hour and Deepgram lets one token open any number of connections, which stay open after the hour. Someone who pulled the token out of the app could stream audio for hours and never report a minute, so the allowance never runs out and tokens keep coming (60 an hour per account). Making the token short would force a fresh token on every reconnect, which slows listening, so instead the server now watches the bill: once an hour it reads today's Deepgram hours and emails the owner once a day if they pass 12 hours (normal is under 4). Server commit below, 3 new tests. The server's own key can read Deepgram usage (checked, 200).
2. **Mock interview could fall back to a fake key.** When Speechmatics gave no key, the server sends the stand-in "speech-backup-unavailable". The website's mock interview passed it to the browser as a real key, so a Deepgram failure fell back to a connection that could only be refused. The route now drops the stand-in. New check scripts/check-speech-placeholder.mjs keeps the word the same on both sides.

Real cost today, read only: Deepgram transcribed 7.9 hours in the last 14 days (about 4 US dollars). 32 real signups in 30 days, 2 used more than 40 answers, 96 listening minutes reported in total, 108 guest devices ever. No sign of abuse.

The 1 day trial, in numbers: one throwaway account can take at most 50 answers (about 10 US cents of AI), 120 reported listening minutes (about 1 dollar of speech) and one free Maya interview. Ten fake accounts a day would cost around 10 to 15 dollars a day. Options: (a) leave it as is and rely on the new spend email (recommended now, nothing is being abused); (b) require a verified email before the trial (stops scripts cheaply, adds one step for real people); (c) a few trials per address a day (can turn away an office or a mobile network sharing one address).

Checks: backend 180 tests, 0 failed; website tsc clean, check-sync ALL IN SYNC, all 10 script checks pass. No Windows code changed.

## Round 31, 2026-11-07: account deletion and what is left behind

Apple needs account deletion to work, and the privacy page promises a finished deletion within 30 days. A person asks
from the phone app (POST /api/v1/account/delete-request) and the owner completes it with Delete in the admin user list.
Production today, read only: 1 request ever, the Apple reviewer account's, still dismissed (left as it is).

Fixed (website 1db0e8b1, test scripts/check-account-deletion.mjs):
1. Deleting a paying customer left their subscription charging. Delete removed the account but never touched Stripe,
   so the card was charged every month for an account that no longer existed. Delete now cancels every subscription on
   that Stripe customer that still charges (plan, Practice, any spare) before anything is removed. If Stripe cannot be
   reached, nothing is deleted and the owner sees "Could not cancel this account's subscription at Stripe, so nothing
   was deleted. Try again."
2. A late "subscription ended" event from Stripe brought a deleted account back as a blank Free record (the webhook
   writes with merge). It now does nothing when the account no longer exists.
3. Deletion requests were invisible in the admin panel. The only sign was one email, and nothing marked a request done,
   so the 30 day promise depended on that email. Waiting requests now show at the top of the admin page with how many
   days they have waited, and Delete marks the request completed and removes its email.
4. Personal data left behind after Delete: problem reports (client_errors) kept the person's email in "user", and the
   billing rows (usage_events) kept it in "email". Problem reports are now deleted and billing rows keep their numbers
   but lose the email.

Checked and fine: interview history and the old usage log are deleted by email, resumes in postgres are deleted through
the backend first (Delete stops if that fails), the Firebase sign-in is disabled first then deleted, companion memory
ends within 2 hours, the database rules stop a deleted person writing anything, and Delete is owner only and audited.

Gaps between the privacy page and reality, for the owner:
- Daily database backups (14 days on the server, 30 days on the laptop) still hold a deleted person's resume until they
  roll off. The privacy page does not say backups are kept that long.
- Stripe keeps its own customer record and receipts; that is a legal billing record and is fine, but not mentioned.
- The page tells people to email support to delete; the phone app has a Delete my account button, and the website and
  Windows app have none.
Owner steps: deploy the website for these fixes; consider one privacy line about backups (up to 30 days) and Stripe
billing records; decide whether the website Account page should get its own delete request button.

## Round 32, 2026-11-07: accessibility and phone width on the signup and money paths

Checked on the live site at phone width (375 wide), read only, and in the code.

Fixed:
- **The sign-in box could not be closed or finished on a short phone.** On a 375 by 548 screen (an iPhone SE in Safari) the box is 677 tall, the page behind it is locked, and the box itself did not scroll. Its close button sat above the top of the screen and the bottom was cut off. With the keyboard open, "Create account" was hidden under the keyboard on most phones. The box now never grows taller than the screen and scrolls inside itself. Checked by applying the same style to the live page: the box fits (16 to 532 of 548), the close button shows and "Create account" can be scrolled to. Website.
- **The first-run guide that every new signup sees had the same problem.** On a phone its three steps stack into a box taller than the screen, so its close button and "I will explore myself" could not be reached. Same fix. Website.
- **Windows sign-in window was silent to screen readers.** The email and password boxes and the close and show password buttons had no names, so Narrator said "edit" or "button" with nothing else. They now have names, and the show password button's name follows its tooltip. Nothing visible changed.

Test: `scripts/check-dialog-fit.mjs` (fails on the old code, passes now) and CleanerTests section 31.

Checked and fine: no page scrolls sideways at 375 wide (home, pricing, start, and the signed-in pages redirect to sign-in); the pricing buy buttons are a normal size; every input on these pages has a label; Escape closes the sign-in box and Tab stays inside it; Enter signs in on Windows from either field.

Skipped: "Try live copilot now" at the bottom of /pricing is a 20 pixel tall text link, small but tappable. The Windows main window has no screen reader names on its icon buttons either; it is large and part of the locked top bar, so it is left for a separate change the owner asks for.

Needs: a website deploy, and the next Windows version.

## Round 33, 2026-11-07: caching and data leaking between accounts

**Fixed (Windows): a second account on the same Windows login saw the first account's past interviews.** Local transcripts are saved in one folder per Windows user, and the Sessions list showed every file in it. When a different Replysis account signed in on the same Windows login (a shared family PC, or someone switching accounts), Sessions listed the first account's questions and answers. New session files now carry a short account tag (made from the account id, not the id itself), and the list shows only the signed-in account's files. Files written before this have no tag and are listed as before, because nothing says whose they are. Files: `SessionAccount.cs` (new), `MainWindow.xaml.cs` (session header), `SessionsPanel.xaml.cs` (filter), `tests/CleanerTests/SessionAccountTests.cs` (14 checks).

**Checked and fine:**
- Website: every per-user API route is dynamic (Next 16 does not cache route handlers that read the request), and pages like /pricing are cached as static pages with no account data in them. No shared cache sits in front of the site: nginx has no proxy cache and the API location sets `proxy_cache off`.
- Website memory caches (Maya and voice allow caches, rate limits, listening report counts) are keyed by account or address, never shared between accounts. A "yes" for Maya lasts up to a minute after an account runs out, by design.
- Server memory: screenshots handed over ahead of a question are stored with their owner and refused to anyone else; the read-ahead check is keyed by account; the phone answer feed is keyed by the verified account.
- The standby server copy only takes traffic when the main one fails (nginx `backup`), so the two copies never split one person's requests and their memory never disagrees in use.

**Checks:** app and all test projects build with 0 errors; CleanerTests all passed, including section 32. No website or server code changed this round.

**Needs the owner:** ships with the next Windows version.

## Round 34: dead code and risky leftovers (2026-11-07)

Removed, after checking every client (website, Windows, Mac, phone app, server) calls none of it:
- Website `app/lib/credits.ts`: six old helpers that wrote answer balances and plans straight to the database from the browser (create the account record, take answers, upgrade a plan, cancel a plan, and two readers only they used). Nothing called them, and the database rules put live today refuse those writes anyway. Left in, they invited someone to wire a browser-side charge or plan change back in. The sync check now fails if that file ever writes again.
- Website page `/real-interview/popup`: an old floating interview window that read a resume and settings from the web address. Nothing opens it any more (the interview page passes its settings another way), so it was a page a crafted link could still open with someone else's text.

Left in place, on purpose:
- Server `/api/v1/resume/analyze` and `/api/v1/resume/load/{id}`: no app calls them today, but both check the sign-in, charge and refund correctly, and only return the caller's own resume. Removing them gains little.
- Server `/api/v1/voice/*`: the phone call feature is paused, not dropped.
- Windows: the hidden Interview and Practice switch in the main window XAML (top bar area, locked), and the developer-only screen capture files (only in Debug builds, never shipped).
- No website API route was unused: every one has a caller.

Checks: website type check clean and a full production build passes (from a separate copy, so the other chat's dev server was untouched; its stale generated files still name the removed page until it rebuilds), sync check ALL IN SYNC, all 11 script checks pass. No server or Windows code changed.

Needs the owner: a website deploy.

## Round 35, 2026-11-07: everything waiting to deploy, checked together

What I checked: the 13 website commits and 7 server commits since the Nov 4 deploy, read as one change. The new ones since round 23 (rules check, emails, admin numbers, speech stand-in, account deletion, phone fit, dead code, older app alerts, failed email retries, speech spend email) don't fight each other:
- Payment webhook: the deletion guard (round 31) only stops a deleted account being recreated; the refund rules from round 11 are untouched.
- Customer emails: the 12 hour gap (round 27) and the trial ended wording (round 2) sit in the same job and agree; the server's trial reminder uses the same claim format, so the gap sees it.
- Problem report alerts: rounds 17 and 26 both only make more reports minor. The server's copy of the old app's naming matches what Windows 1.0.52 really sends.
- Phone and Mac apps: Maya, her voice, speech keys and the out of answers text behave as before for them. The free mock interview rules on the website and server weren't touched, so they still match.
- replysisv@gmail.com: still an internal Max account; nothing pending changes how it works.
- Speed: nothing new on the answer path.

No regressions found, so no code changed this round.

Checks, all run today:
- Website: type check clean, full production build passes (from a separate copy, with dummy public settings, so the other chat's dev server wasn't touched), sync check ALL IN SYNC, all 11 script checks pass.
- Server: 180 tests run, 0 failed.
- Payment test (local database copy and Stripe test mode only; a probe confirmed nothing reached the live database; email off; the server's resume delete went to a local stand-in): 71 of 71 pass. That is the 35 webhook checks, 6 signed in checks, 17 round 13 checks, and 13 new round 31 checks:
  - deleting an account cancels its live Stripe subscription, removes the record, marks the request completed without its email, deletes problem reports with that email, keeps billing rows without the email, and deletes resumes through the server;
  - Stripe's late "subscription ended" and "subscription updated" events don't bring the deleted account back;
  - the admin page lists waiting requests only, never dismissed or completed ones;
  - if Stripe can't be reached, nothing is deleted; someone who isn't the owner is refused.
- One harness note: the Maya refund check needs the test Maya price passed to the test website; with it, it passes.
- Test subscriptions from this run were cancelled; the local servers and database copy are stopped.

Waiting in the next website and server deploy, in plain words:
- Sign in links can't send people to another site.
- Emails: an ended trial gets "Your free trial has ended"; no "Hi User"; at most one email in 12 hours; no Maya sales email to paying customers; a failed send is tried again; the trial reminder skips unsubscribed and test accounts.
- Resume PDF keeps all text; browser interview: mic turns off, no double listening, one saved copy, the AI gets the whole resume and full instructions.
- Payments: refunding Maya removes only Maya; bank approved top ups can be refunded; a charged top up always adds its minutes.
- Account deletion cancels the subscription first, removes leftover emails, and the admin page shows waiting requests.
- Admin numbers: revenue, conversion, India sales and paying customers counted correctly.
- Server: no paid screen read ahead for empty accounts; screen answers about 1 s faster on screens without code; voice answer timings record the app; older Windows apps no longer trigger false outage emails; a daily email if speech use passes 12 hours.
- Phone fit for the sign in box and first run guide; removed old unused code.

Go or no go: GO. Deploy the server first (it stops the old apps' false alerts at once), then the website. Still not tried for real: the browser interview fixes in a real browser and the faster screen answers against the live AI.

## Round 36: pending website fixes tried in a real browser (2026-11-07, stopped after 3 hours)

Ran a separate copy of the website against local test databases (no live data, no money). Tried and passed: sign up shows the 1 day free trial, and the Account page shows "Free trial", "for 1 day" and the right end date.

Found and fixed: the name typed at sign up never reached the account record, so the account kept the "User" placeholder (and emails said "Hi User"). Sign up set the name and then sent the old sign in token, which has no name in it. The token is now refreshed after the name is set. Check: scripts/check-signup-name.mjs. Needs a website deploy.

Not finished (the round ran too long and was stopped): pricing buy button in Stripe test mode, the browser interview Space and End session checks, resume PDF preview, admin deletion requests, the phone size sign in box. These still need a real browser try.

## Round 37: browser checks stopped (2026-11-07)

Stopped after its time box. The website's page guard checks sign in tokens against Google's real keys, and the local test database's tokens are not signed by Google, so signed in pages cannot be opened against the local test copy without changing the site. No code was changed and nothing was committed. The remaining browser checks (pricing buy button in Stripe test mode, phone size sign in box, resume preview, admin deletion requests, browser interview Space) are best done once on the live site after the next deploy with a real test account, about 10 minutes of clicking.

## Round 38: dates and time edges (2026-11-07)

Found and fixed: paying customers were told the wrong refresh date.
- Windows: the answers window always said the 1st of next month. A monthly subscription refills on its own renewal day, so a customer who subscribed on the 15th was told "Dec 1" for a refill on Dec 15. The server now sends the date (refreshesAt) and the app shows it in local time; with no date (older server) it still shows the 1st.
- Website: the Account page said "Refreshes" with the stored safety date, three days after the real renewal ("Dec 18" for Dec 15). It now shows the renewal day for a monthly subscription. Annual plans, India months and everything else are unchanged.
- The server and website use the same rule (FirestoreCreditsService.refreshShown and app/lib/refresh-date.ts).

Checked and fine: month rollover from the 29th to the 31st and February (both sides set the date to the 1st first), trial and India month end dates are stored the same way on both sides and compared as times, Stripe period ends are seconds on both sides, Windows speech key expiry counts from the moment it arrives so a PC with the wrong clock still renews it, and free accounts are never promised a refill.

Checks: server tests 181 run, 0 failed (1 new). Website type check clean apart from the known stale popup page entries, sync check ALL IN SYNC, all 13 script checks pass (1 new). Windows build 0 errors, CleanerTests all passed (5 new). Needs a server and website deploy, and the next Windows version.

## Round 39: crashes and what the owner sees (2026-11-07)

Found and fixed (Windows):
- The worst crash never reached admin. A crash on a background thread ends the app the moment its handler returns, and the report to the server was sent in the background without waiting, so it almost never got out. The owner only ever saw crashes the app survived. Now that path waits up to two seconds for the report.
- The crash marker was written but never read. The app wrote crash.flag when it had to close, but nothing looked at it again, so if the report did not get out (offline, or the process died first) nobody ever knew. Now a background crash writes the marker too, and the next launch reports "previous run crashed" once, quietly, and removes it. It counts as a real error for the outage email (which still needs two different computers in ten minutes).
- Tests: section 33 in CleanerTests (marker written, read once, cut short if damaged, never throws; launch reports it; fatal path waits).

Checked and fine:
- Crashes the app can carry on from are logged and reported without a dialog; only memory exhaustion and broken installs close it, with a plain message.
- The debug log is written straight to disk on every line, rotates at 20 MB, and the previous run's log is kept as the old log, so a crash can still be read after a restart.
- The engine exiting with any code other than 1 or 2 is treated as a crash and restarted (round 1.0.52 and the 11-02 audit).
- The website never returns a stack trace: API routes answer with fixed messages, and the server has error messages and stack traces switched off.

Not covered: a hard UI freeze (the window stops responding but nothing throws) has no watchdog and no report. Adding one is a larger change for a later round.

Ships with the next Windows version.

## Round 40: a window that stops responding (2026-11-07)

Found and fixed (Windows):
- Closing the app always waited 3 seconds and lost the last answer's cloud copy when a cloud sync was still going. The close waits on the sync from the window's own thread, and the sync needed that same thread to carry on, so neither could move until the 3 second limit. Every step of the sync now carries on without the window's thread, so the wait ends as soon as the sync does. Nothing in the sync touches the window.
- A hard freeze was invisible. Nothing throws, so no crash handler saw it. Now a small background watchdog sends an empty job to the window every 2 seconds. If one waits more than 10 seconds, the debug log says so once with the tag of the last thing logged (never any text), and a freeze.flag is written. When the window answers again the flag goes and one quiet report is sent: under 30 seconds is "ui-freeze-10s-minor" (shows in admin, never in the outage email), 30 seconds or more is "ui-freeze-30s" or "ui-freeze-60s" (counts, still needs two computers). If the app is closed while frozen, the next launch reports "previous-run-froze" once. No popup, no change to the look, one empty job every 2 seconds.
- Tests: section 34 in CleanerTests (kinds, the freeze decision on a fake clock, the flag, launch wiring, every cloud sync await free of the window thread).

Checked and fine: the other waits on the window's thread are bounded and only on the way out (presence goodbye 1.8 s, session file drain 5 s, recording save) or at start before the window exists (waiting for a closing copy, 8 s). Screen reading's text recognition runs off the window's thread. No backend change was needed: the server already treats a new kind as real and needs two computers, and "-minor" keeps short stalls out of the email.

## Round 41: second regression review of the Windows app (2026-11-07)

Read everything committed since round 22 together (rounds 32, 33, 38, 39, 40). Two real problems, both fixed:

1. **Sleep looked like a freeze.** The new freeze watchdog measured time with the clock, and sleep stops the app and the watchdog together. After a night's sleep the first check saw a job "waiting" for hours and would have sent a 60 second freeze report, which counts toward the outage email: two laptops waking within ten minutes would have emailed the owner. A long gap between the watchdog's own checks now means the computer slept, and that wait is dropped. A freeze already noticed when the computer slept is dropped too, and its freeze.flag is removed, so the next launch does not report it.
2. **Sign out kept the refresh date.** The refill date added in round 38 was not cleared on sign out, so a second account signed in on the same PC was shown the first account's date until its own balance loaded. Now cleared.

Also tidied a doc comment that round 38 split in PlanFacts.cs. Checked and fine: the cloud sync change (nothing it touches belongs to the window), crash marker and freeze flag on the next launch (each reported once, only by the copy that owns the lock), account tags on session files (untagged older files still show, a file is tagged with the account that started it), the refresh date when today's live server sends none (falls back to the 1st), sign in screen reader names, and every new report kind against the server's alert rule (ui-freeze-10s-minor never alerts; previous-run-crashed and previous-run-froze need two computers).

Checks: app and all 3 tracked test projects build with 0 errors; CleanerTests all passed (5 new checks); engine test files pass except the same 3 old failures in test_line_resilience.py.

### What the next Windows version contains (everything since 1.0.52, in plain words)

- **Listening:** the start of a question is kept when the connection drops, no repeated words after a reconnect, faster recovery after sleep, listening time is kept when a report fails, and other languages recover from a speech service error.
- **Answers:** no sign out on a slow sign in refresh, a 20 second limit when no words come, Thinking never sticks, a trial that runs out gets the Upgrade button, Auto no longer slows down in long interviews.
- **Auto and keyboard:** Auto does not restart after Finish and New session resumes it; spaces work in every text box; indenting code in another app does not send answers; code keeps its indentation in the small answer window.
- **Screen share:** the black box stays during screen reads, and the right monitor is read on two screens.
- **Settings:** Save does not reset other settings, and an unplugged microphone is remembered.
- **Plans:** the trial shows as Free trial with the right numbers; paying customers see their real refill day.
- **First run:** clear advice when antivirus removes the speech engine or the Store microphone switch is off; Store package asks for the microphone; Thai and other calendars send correct dates.
- **Updates:** update notices never stick and never show during an interview.
- **Privacy and safety:** the audio pipes are readable by this Windows user only; past interviews show only for the account that recorded them; opening Replysis again brings back a hidden window; screen readers can read the sign in window.
- **Owner visibility:** no false outage emails from customers running out, wrong passwords, failed updates, information banners or sleep; serious crashes and freezes now reach admin.
- **Before release:** rebuild engine-dist, one try each on a black box laptop, a second monitor, a Store install's microphone switch and Swathi's laptop, then the owner's yes.

## Round 42: website and server changes since round 35 (2026-11-07)

Re-read the sign up name change (rounds 36), the refresh date (round 38) and everything else committed since round 35.

Found and fixed: the round 36 sign up fix added a fresh sign in token request after the name is saved. If that request, or saving the name, failed (slow phone, a blip at Google), sign up showed an error even though the account had already been made and the person was signed in. The name step is now best effort: if it fails, sign up still finishes and the account keeps the "User" placeholder, which the emails already skip. Website commit 6a200125. Check: scripts/check-signup-name.mjs now fails on the old code.

Checked and fine: Google sign in does not touch this code. The refresh date reply from the server: guests never reach it, an account with no reset date sends no date (older apps and the iPhone and Mac apps ignore the new field), the renewal day is stored in seconds and shown as a date. The Account page reads it the same way.

Checks: type check and production build pass from a separate copy, sync check ALL IN SYNC, all 14 script checks pass, server tests 181 run, 0 failed.

Waiting in the next website and server deploy (everything since the Nov 4 deploy):
- Sign in: after sign in links stay on replysis.com; the name typed at sign up is saved, and a failed name step never blocks sign up.
- Emails: trial ended wording, no "Hi User", at most one email in 12 hours, no Maya sales email to paying customers, failed sends retried, unsubscribed and test accounts skipped.
- Resume PDF and browser interview: no lost text, mic turns off, no double listening, one save, whole resume and full instructions to the AI.
- Payments and refunds: refunding Maya removes only Maya, bank approved top ups can be refunded, a charged top up always adds its minutes.
- Account deletion: cancels the subscription first, removes leftover emails, waiting requests shown in admin.
- Admin numbers: revenue, conversion, India sales and paying customers counted correctly.
- Account page: monthly subscribers see their real renewal day.
- Server: no paid screen read ahead for empty accounts, screen answers about 1 second faster on non code screens, voice timings record the app, older Windows apps stop false outage emails, a daily email if speech use passes 12 hours, the credits reply includes the refresh date.
- Phone layout: sign in box and first run guide fit small phones. Old unused code removed.

Go or no go: GO. Server first, then website. Not yet tried for real: the browser interview fixes and the faster screen answers; a 10 minute click through on the live site after deploy covers them.

## Round 43: local currency prices and country detection (2026-11-07)

Every live Stripe price the website uses was read (read only) and compared with what the pages show. All 31 match, and every one is active:
Pro USD 34.99, INR 699, GBP 27.99, CAD 47.99, AUD 52.99. Max USD 79.99, INR 1299, GBP 62.99, CAD 109.99, AUD 119.99.
Maya add-on USD 10, INR 399 (monthly and once), GBP 7.99, CAD 13.99, AUD 14.99. Practice USD 14.99, INR 499 (monthly and once), GBP 11.99, CAD 19.99, AUD 21.99.
Answer packs USD 9.99, 24.99, 69.99 and INR 149, 349, 899. India minutes are charged from the same table the page shows.
The page and the checkout both decide the country from the same address (the server's country database), so a VPN or travel changes both together, never one.

Found and fixed (website 06a7b15a):
1. The pricing table's Maya row always said "Add for $10/month". A visitor in India was charged Rs 399, in the UK GBP 7.99, in Canada CA$13.99, in Australia A$14.99, while the same page's plan cards already showed their own currency. The row now shows the add-on in the visitor's currency.
2. The home page's plan facts always said $14.99, $34.99 and $79.99. India pays Rs 699 and Rs 1299 for one month, and the UK, Canada and Australia pay their own prices. They now show the visitor's currency, from the same country check as the pricing page.
Both now read one helper (app/lib/display-prices.ts). The new check (scripts/check-display-prices.mjs) fails on the old code, and holds the shown prices to the live Stripe amounts above.

Left as is:
- A customer who once had a subscription in one currency and later buys from another country (a US subscriber who moves to the UK) is refused by Stripe, because a Stripe customer keeps one currency. Rare; the checkout shows "Could not start checkout". Fixing it means a second Stripe customer, which the 2026-11-04 fixes deliberately avoid.
- The page shows a local currency when both its Pro and Max prices exist; the Maya and Practice prices in that currency are checked separately at checkout. Today all are set, so nothing differs.
- The Windows app opens replysis.com/pricing with no country in the link; the site decides from the address, which is right.

## Round 44: deploy, rollback and backup scripts (2026-11-08)

Read the live deploy scripts, timers and backup jobs on the server without changing anything, and fixed the laptop backup copy.

Fixed on this laptop (C:\Users\krish\Desktop\replysis-backups\pull-postgres.ps1, not in git):
1. A cut-off copy was kept as a backup forever. If the copy from the server broke half way, the partial file stayed under the final name, so the next day's run saw it, skipped the copy, and the folder held a broken backup. Copies are now written as .part, checked as a whole gzip file, and only then given their name; leftovers are removed.
2. A stopped server backup looked fine. If the server's daily backup stopped, the laptop kept copying the same old file and logging "ok". It now logs STALE when the newest dump is over two days old.
Tested with a dry run against a temp folder and against the real folder (nothing written).

Proposed for the server, waiting for the owner's yes, in docs/SERVER_SCRIPT_FIXES.md:
1. A failed git pull (backend) or fetch (website) still builds the old code and reports the deploy as done.
2. The website keeps no rollback image (the backend does).
3. An aborted deploy leaves the standby copy on the broken build with nothing behind the primary.
4. No free disk check before a build (71% used).

Checked and fine: all seven containers come back after a reboot (restart always plus podman-restart with linger), certificate renewal, postgres backup and follow-up timers are running, the backend rollback image exists, build failures touch nothing.

## Round 45: the Microsoft Store copy (2026-11-07)

No real bug found in code, so nothing changed. What was checked, read from the code, the package manifest and build-windows-msix.yml:

- Files: settings, sign in, past interviews, the debug log, latest.txt and the crash and freeze flags all live under the user's local app data folder. In the Store copy Windows keeps these in a private folder for the package. The speech engine is started from inside the package, so it sees the same private folder as the app, and it only writes there (never next to itself in the read only install folder). Uninstalling the Store copy removes these files, which is the right privacy behaviour.
- The audio pipes from round 18 work inside the package, because the app and the engine are in the same package and run as the same user.
- Google sign in: the Store copy runs with full trust, so the 127.0.0.1 sign in listener is allowed. The log already records "packaged=" for each Google sign in, which is the line to read if one ever fails.
- No start on login feature exists, so the packaged startup rules do not apply.
- Updates: the Store copy never runs the website updater; it asks the Store, a mandatory update shows the launch gate, an optional one is installed by the Store when the app is closed, and the top bar notice only opens the Store page. Nothing interrupts a session.
- Build: the workflow builds the speech engine first, refuses to finish without it, opens the bundle to confirm the engine is inside, stamps the same version into the package and the app, and leaves the package unsigned for Partner Center as Microsoft wants.

Low, not changed: the optional sideload .appinstaller the workflow writes names the package by an old ID instead of Varoxel.Replysis, so it would not install. Nothing uses it (no server secrets, no link on the website). If sideloading is ever wanted, that name has to be fixed first.

Check on a real Store install before release (one short session):
1. Microphone: Windows Settings, Privacy, Microphone shows Replysis; switching it off shows the quiet microphone message, switching it on lets listening start (round 16).
2. Google sign in completes.
3. Start, ask one question, get an answer; Finish; reopen the app and the past interview is listed.
4. If anything fails, the debug log is at %LOCALAPPDATA%\Packages\Varoxel.Replysis_<id>\LocalCache\Local\InterviewCopilot\debug.log, not the usual folder.

## Round 46: corners of the Windows app no round had read (2026-11-07)

Found and fixed one bug:
- One question the server refused for size switched answer compression off for the rest of the session. The app sends each question compressed, and when the server refuses a compressed request it sends it once plain and stops compressing. The server also refuses an oversized question the same way (before any charge), and the plain copy is refused too, so compression went off for nothing and every later question uploaded about twice the bytes, which is slower on a weak line. Compression now stays off only when the plain copy actually got through. Test in CleanerTests (InstantResponseTests).

Corners read and found fine, so later rounds can skip them: AnswerHistory (back and forward through answers), UplinkGovernor and PacedUpload (screenshot sent ahead, line test, pacing behind the voice), OAuthCallbackReader (Google sign in answer: state check, size cap, silent connections), UserProfileSync, AlwaysReady (keep awake: every call is on the window's thread, so the hold is released), RecoveryPolicy, StoreUpdateRules, ResumeFileSafety (size and expanded size caps), DeviceIdentity, ResumeParser contact detail removal.

Not read this round (for later): PromptBuilder (2,141 lines), StoreUpdateService, AppUpdates, StealthDialog, ScreenOcr and ScreenOcrLayout, CompanionLink, UpdateRequiredWindow, UpdateDownloadGuard, RegionCaptureWindow, AnswerClosers, StealthSelfTest, AudioSourceRules, RemoteAppConfig, EngineWarmup, SttRenewalRules.

## Round 47: the remaining unread Windows files (2026-11-07)

Fixed: a long question lost its actual question. The prompt caps the question at 4,000 characters and kept the start, cutting the end. When an interviewer reads out a long brief and asks at the end ("...so design the backend for that") and no single sentence looks like a question, the whole run reaches the prompt, so the model got the brief and never the question. The question now keeps its end, marked "[earlier part cut]", and the saved history does the same. No prompt got longer, so answers are no slower. File PromptBuilder.cs, test section 35 (PromptTruncationTests).

Read and found fine: PromptBuilder (history cap of 20 turns with 4 sent, code in older turns collapsed, screen context only for 10 minutes), StoreUpdateService and AppUpdates (the launch gate is switched off, the Store notice opens the Store page, nothing installs during a session), UpdateRequiredWindow, UpdateDownloadGuard (stall retry), ScreenOcrLayout, CompanionLink (phone Stop and Answer run on the window's thread), AnswerClosers, AudioSourceRules, RemoteAppConfig (only whole number REPLYSIS_ engine settings are taken), EngineWarmup, SttRenewalRules. Not read in depth this round: StealthDialog, StealthSelfTest, RegionCaptureWindow (small, screen only), ScreenOcr.

Small notes, not changed: LastEntryWasScreenAnalysis is no longer called by anything, and a spoken question containing the word "screen" skips the personal fact lock for that turn only.

## Round 48: stealth dialog, stealth self-test, area picker, screen text reader (2026-11-07)

No real bug found; no code changed.

Read end to end: StealthDialog.cs, StealthSelfTest.cs, RegionCaptureWindow.xaml.cs, ScreenOcr.cs (with the callers in MainWindow and ScreenAnalyzer).

Checked and fine:
- Every dialog and the area picker get the same hiding as the other windows, applied when the window is created, so they are never in a screen share while hiding works.
- The self-test hides its test square first and gives up ("unknown", then the answer window's own setting decides) when the square itself cannot be hidden, so it does not report "hidden" for a window Windows refused. A visible or black box result only changes the quiet Screen share row and sends one report, never a popup.
- The area picker turns the picked corners into real screen pixels, so the captured part matches what was drawn on any scaling.
- The screen text reader sends only the words of the area that was captured, logs only lengths and error types (never screen words), and on this laptop reports a maximum image side of 10,000 pixels, so a 1.5x enlarged 4K screen is still read. On a Windows language with no text reader it quietly falls back to English, and if that is missing too it sends pictures as before.

Needs a real laptop try (cannot be proven from code):
- The area picker on two monitors with different scaling (100% beside 150%). Windows may resize the picker when it lands across both, which would leave a strip that cannot be picked.
- The self-test on a slow laptop: it waits a fixed 350 ms for the test square to be drawn; if drawing takes longer, the result is "hidden" without proof.
