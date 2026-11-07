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
