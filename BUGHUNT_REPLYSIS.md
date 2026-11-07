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
