# Mac handoff: everything from 2026-10-27

From the Windows / website session. Read all of it before changing the Mac app. Each item says whether it is already
done on the server or website (nothing for Mac to build) or needs a Mac change. The owner's rules are at the end.

## 1. Plans and pricing (server and website: done; Mac: copy only)

- Paid plans never show answer or interview numbers to customers, and never say "unlimited". People read a number as a
  small limit. Limits are still enforced on the server, and a person's own balance can still show in their account.
  The free plan may keep its small number ("5 answers to try it, once").
- Plans now: Starter (free), **Practice $14.99 a month** (Rs 499 in India, GBP 11.99, CAD 19.99, AUD 21.99): mock
  interviews with Maya plus live answers. **Pro $34.99**, can add **Maya for +$10 a month**. **Max $79.99 includes Maya**.
- Wording for paid plans: Pro "Answers for an active job search, refreshed every month"; Max "Our largest monthly
  allowance, for interview heavy weeks"; Practice "Practice interviews with Maya every month" and "Live answers every month".
- Prices show in the visitor's own currency only (the website asks `/api/geo`). Stripe Adaptive Pricing is on, so the
  Stripe checkout page shows about 150 local currencies. Mac: never show "$14.99 (Rs 499 in India)" side by side.
- Mac change: if the Mac app shows any plan text or numbers, make it match the above. Buttons go to
  `https://replysis.com/pricing` (the Mac app is sold outside the App Store, so linking out to pay is allowed).

## 2. The 3 day free trial is never "Pro" (server: done; Mac: needs a change)

- Every new account starts on a 3 day free trial: 50 answers, no card. In the database it is `plan: "pro"`,
  `planSource: "trial"`. The owner: "that's not Pro, it's just the free tier".
- New today: `GET /api/v1/interview/credits` replies with **`"trial": true`** for these accounts (alongside `"practice"`).
- Mac change: when `trial` is true, show **"Free trial"** and the answers count, never the Pro badge. All wording says
  "3 day free trial", never "3 days of Pro" or "Pro trial". Emails were changed on the server.

## 3. Free answers run out: "Upgrade now" (Windows: done locally, not released; Mac: needs the same)

- When the free answers or the free trial run out, the message says Pro gives "a whole month of answers, enough for many
  interviews, and you can cancel any time" (no numbers), and the banner button reads **"Upgrade now"** and opens
  `https://replysis.com/pricing`. Pressing for an answer with nothing left also shows that banner once, so there is
  always a direct link. Windows commits 8d561c1 and 2cb20dd (in MAC_CATCHUP.md).

## 4. Maya, the live video interviewer (server and website: done; Mac: only if Mac has its own Maya screen)

- Live face is "blazer Maya": Simli legacy face `5894f072-8143-49f1-9cc4-e514dcaa9345`, env `SIMLI_FACE_FIT=cover`
  (she fills the whole window; the session reply carries `fit: "cover"`).
- Fixed today, all in the website's `useMayaLive` and `/maya-embed`, so every app that uses them gets it:
  - Simli hung up after 2 minutes without her voice (she is silent while the candidate answers). Idle time is now the
    session length.
  - A dropped call rejoins by itself (up to 3 times). A free interview may rejoin for its remaining time.
  - Her voice started before her face appeared. Her lines now wait for her face, up to 30 seconds.
- The phone app shows `/maya-embed` in a WebView: send `{type:"start", token}`, then `{type:"speak", id, rate, pcm}`
  (base64 16 bit mono PCM, 16 kHz is best for lip sync); it answers `{type:"status"}` and `{type:"done", id}`. **Wait
  for status "ready" before sending her first line.** Mac: if Mac has a Maya screen, do exactly this.
- Her replies are natural (server prompt): a short real answer like "My name is Pawan" gets "Nice to meet you, Pawan..."
  and a request for more, never "I didn't catch that". Only clearly jumbled text is treated as noise. The "stronger
  answer" in feedback uses the candidate's name and facts, never placeholders like [Your Name].
- Free Maya: one interview per account, 3 questions, at most 5 minutes, counted only when it starts. At most 3 free
  interviews a day across everyone ("Today's free interviews are taken" plus Upgrade now). Accounts with `tester: true`
  in Firestore do not use up the day's free interviews.

## 5. Listening limits (server: done)

- Listening tokens: **60 an hour per account** (was 12). A website practice interview mints one per answer, so 12
  locked people out after two interviews in an hour. Audio is still metered separately.
- Website listening minutes are reported with a fresh sign-in token (an hour old token made the server drop them).
- The false "answers are failing" alert (screen reads with nothing to answer) no longer emails the owner.

## 6. Website pages (done)

- Privacy, Cookies, Terms and Trust updated: Maya (Google Cloud Text-to-Speech voice, Simli video, the camera never
  leaves the screen), the phone app, visit counting, product emails with unsubscribe, the real cookies
  (`replysis_session`, Stripe's on its checkout), Practice, the add-on, fair use. Mac links to these pages need no change.
- Pricing page top bar: "Only want practice interviews with Maya? Practice is $14.99 a month. Buying Pro? Add Maya for
  only $10 more. Max includes her." The Replysis logo on app pages goes to the home page.

## 7. Windows commits not yet pushed (release gate)

af21c4b Practice label, 8d561c1 + 2cb20dd Upgrade now, 3efeb23 MAC_CATCHUP. Their notes are in MAC_CATCHUP.md.

## Owner's rules (keep them)

- No dashes, middle dots, taglines or "cheating" in visible text. No alarming popups.
- No numbers on paid plans, never "unlimited". The trial is "Free trial", never Pro.
- Never release or publish without the owner's yes, and only after Windows and Mac both sign off.
- Never blame the network in messages to the owner or customers.
- Short, simple answers to the owner.
