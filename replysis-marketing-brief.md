# Replysis social-media marketing brief

Research date: September 19, 2026.

## Evidence labels and scope

Every statement below uses one of these labels, either individually or through its section heading:

- **[V] Verified in the project:** confirmed in inspected source, assets, configuration, or release metadata. For features, this means implementation evidence—not a fresh successful end-to-end test.
- **[W] Present on the website but not technically verified:** published website, Store, or release-page claims without sufficient runtime verification.
- **[R] Recommended marketing language:** proposed positioning, creative direction, or ready-to-paste copy; not a statement that an account, campaign, or result already exists.
- **[C] Needs confirmation from the owner:** deployment, commercial terms, operational evidence, ownership, or platform behavior that remains unresolved.

**[V] Research scope:** Windows source at commit `ee0c660`, website source at `15e0e892` plus existing local changes, backend source at `773b121` plus existing local changes, the local Mac checkout at `ba7f2ba`, live public pages, GitHub release metadata, Microsoft Store listing, and branding assets were inspected. Older CopilotX folders were not treated as the current website.

**[C] Important boundary:** this was read-only marketing research, not release certification. No account creation, paid checkout, credit-consuming AI requests, microphone capture, live meeting tests, installer execution, or fresh build/test run was performed. Therefore, “every currently working feature” cannot honestly be certified. Section 3 inventories implemented features and explicitly excludes known broken or unverified claims.

**[V] Deliverable:** this brief is the only newly created file. No product code, website content, accounts, or configuration was changed, and nothing was committed or pushed.

## 1. What Replysis does

**[V] Factual product explanation:** Replysis is an AI-assisted job-interview workspace combining a browser-based resume editor, mock interview practice, and live answer suggestions. Users provide their resume and target-job context; the software can generate practice questions, provide coaching feedback, transcribe interview audio, and stream suggested responses. The Windows desktop app adds system-audio capture, a compact overlay, manual and automatic listening workflows, and screen-reading assistance for visible questions and coding problems. AI processing uses cloud services, usage is limited by credits and listening allowances, and suggestions require the user’s review. A Mac download is advertised, but the publicly linked release must not be assumed to match the current Windows app.

Evidence: source inventory in Section 16; [public product overview](https://replysis.com/features).

## 2. Target customers

**[R] Primary customer:** an adult, actively interviewing job seeker who wants to organize their experience, rehearse answers, and use contextual AI guidance where interview rules permit it. Technical and knowledge-work candidates are a strong initial focus because resume context, coding-screen analysis, and behavioral practice are prominent features.

**[R] Secondary group 1:** adult graduates and early-career applicants preparing their first structured resume and practicing common interview questions.

**[R] Secondary group 2:** experienced professionals and career changers translating past work into clear, role-relevant examples.

**[C] Positioning boundary:** do not market Replysis as an enterprise hiring platform, recruiter assessment system, school-wide service, or coaching-agency license without confirming those commercial rights and capabilities. The current Terms describe personal, non-commercial job-search use and require users to be at least 18.

## 3. Feature inventory

### Resume tools

**[V] Implemented in the inspected project; not newly exercised against production:**

- Structured resume editing: personal details, summary, experience, projects, education, skills, and certifications.
- Twelve template definitions: Cornerstone, Meridian, Dualaxis, Apex, Density, Pillar, Executive, Prestige, ATS Clean, ATS Minimal, TechPro, and FAANG Elite.
- Live resume preview and controls for fonts, accent color, spacing, section order, and A4/Letter paper.
- PDF, DOCX, and TXT text-import paths. Imported text does not prove perfect automatic reconstruction of every resume layout.
- PDF and Word export handlers connected to authenticated backend endpoints. Production rendering depends on the PDF/document service.
- Authenticated resume-saving and resume-loading backend routes.
- Job-description keyword comparison showing matched and missing terms. This is a local text-matching heuristic, not a real employer ATS evaluation.
- AI resume analysis and job-description tailoring backend routes.
- Resume-context verification in interview setup.
- Resume/job context reused to personalize interview suggestions.

**[C] Do not advertise as working yet:** the individual “rewrite bullet” and “generate summary” actions. Their UI sends `rewrite_bullet` and `generate_summary`, but the inspected AI route accepts neither. The UI attempts a separate credit deduction before making the rejected request. This mismatch exists in the inspected committed route as well as the local working copy. Production impact and refunds require confirmation.

**[W] Qualification:** the website advertises free resume building/PDF export and paid AI tailoring. The inspected tailoring path checks authentication and credits; the paid-plan boundary needs verification before promising exact feature exclusivity.

### Mock interview tools

**[V] Implemented in the inspected website source:**

- Resume- and job-description-based question generation.
- Behavioral and technical practice with selectable difficulty.
- Question-count controls: 5–10 on the free UI and up to 50 on paid-plan UI.
- Free UI options include Easy, Behavioral, and Mixed; paid UI also exposes Medium and Hard.
- Browser text-to-speech reads questions aloud.
- Microphone transcription for spoken answers.
- Per-answer feedback with a coaching score, strengths, improvement suggestions, and an example response.
- An example/script path for very short answers.
- Optional camera self-preview.
- In-session conversation history, question progress, and score summaries.
- A session report using answer text/timing to estimate STAR structure, speaking pace, and filler phrases.
- A searchable practice question bank with company/role filters and browser-local bookmarks. The page explicitly identifies these as representative practice questions, not leaked or official employer questions.

**[C] Boundaries:** scoring is coaching guidance, not a validated hiring assessment. The dedicated mock page’s persistent, cross-session history was not established by the inspected save paths. Do not equate its in-session history with automatically saved cloud reports. Current local edits also mean deployment of the newest report behavior needs confirmation.

### Live interview assistance

**[V] Implemented in Windows and/or the web/server paths as indicated:**

- Live transcription and streaming answer suggestions.
- Resume, target role, company, and job-description context.
- Typed questions and conversational follow-up context.
- Behavioral-answer structure and technical/coding answer handling.
- Windows Auto and Manual listening workflows.
- Windows screen analysis, including selected-region capture, active-screen analysis, and primary-screen analysis.
- Code displayed separately from explanatory prose, with copy controls.
- Screen-context preparation and follow-up handling for partially visible problems.
- Windows screening preferences for matters such as work type, availability, location, and user-supplied work-authorization information.
- Shared account-based credit and listening-time accounting across client routes.
- Speech-provider routing/fallback code; English Windows transcription includes Deepgram and Speechmatics fallback.

**[C] Boundaries:** recognition accuracy, uninterrupted long-call behavior, screen comprehension, answer quality, and response speed need scenario testing. Do not promise that generated experience, dates, metrics, or legal information are correct.

### Desktop features

**[V] Current Windows source includes:**

- Main window and compact answer overlay.
- Compact glass-style action buttons, including Read screen.
- Adjustable transparency, pin/always-on-top behavior, and a bring-to-front shortcut.
- Long-transcript scrolling and bounded overlay layout.
- Resume drag-and-drop/import and previous-resume selection.
- Start/pause listening and microphone-capture settings.
- A real-interview microphone tip in Settings.
- Audio-device and transcription-language controls.
- Local session review, answer/code copying, and local session deletion.
- Optional signed-in cloud session synchronization, off by default in the inspected Windows configuration.
- Optional session-audio saving, off by default.
- Keyboard shortcuts, configurable global screen keys, and diagnostic/update interfaces.
- Direct-installer update support; Store distribution is a separate channel.
- Guest access without a Replysis account.

**[V] Language scope:** Windows exposes 17 language choices, including English, Hindi, Tamil, Telugu, Bengali, Marathi, Urdu, Spanish, French, German, Portuguese, Italian, Mandarin Chinese, Japanese, Korean, Arabic, and Russian.

**[C] Language qualification:** a menu option does not establish production accuracy or availability. Telugu has a Sarvam-specific path and configuration dependency. Do not advertise “17 fully supported languages” without testing.

**[C] Not a shipped claim:** the proposed Interview/Practice toolbar switch discussed previously is not established in the inspected Windows code. Its microphone-capture default is still `true`; do not market the proposed “Interview by default” behavior as released.

### Privacy and security features

**[V] Implemented safeguards:**

- Firebase authentication and server-side identity checks.
- Account-ownership checks, request limits, and input-size limits in relevant routes.
- Stripe-hosted checkout rather than a custom card-entry form handled by Replysis.
- HTTPS production endpoints and secure provider-connection paths.
- Windows DPAPI protection for selected saved local data and completed audio recordings.
- Optional Windows audio saving and cloud synchronization.
- OS-level screen-capture exclusion controls.
- Owner-bound, single-use screenshot-cache entries with a 90-second expiry check.
- Local deletion controls and authenticated account/session-related routes.

**[C] Required qualifications:**

- This is not an independent security audit or proof that every route is secure.
- Capture exclusion is setup-dependent, not universal invisibility.
- Screenshots, transcripts, and resume context can leave the device for AI processing.
- Cache expiry is not proof of physical removal at exactly 90 seconds; cleanup is request-driven.
- Audio cleanup is performed by the app, including at startup; it is not a guaranteed background deletion deadline while the app is closed.
- Audio is written as WAV and subsequently protected; do not claim it is never temporarily plaintext.
- Do not generalize Windows encryption/recording behavior to the publicly linked Mac build.

**[W] Published policy commitments:** Replysis says it does not sell user data, train a Replysis-owned model on user content, or share interview content with employers. These are business/provider-policy commitments—not facts that source inspection alone can prove. See [Privacy](https://replysis.com/privacy) and [Trust](https://replysis.com/trust).

## 4. Platforms, meeting workflows, and downloads

| Item | Status and accurate scope |
|---|---|
| Web | **[V]** Public browser-based workspace exists. Mic/camera functions require appropriate browser permissions and support. No extension is required by the inspected workflow. |
| Windows | **[V]** WPF/.NET Windows application; build targets x64. **[W]** Store requirement is Windows 10 build 17763 or later; website says Windows 10/11. ARM-native compatibility was not established. |
| macOS | **[W]** Website advertises a DMG for Apple Silicon and macOS 12+. **[C]** Exact minimum OS, Intel support, current signing, and release parity must be verified on the actual Mac build. |
| Linux / iOS / Android | **[C]** No native app verified. Do not turn browser access or phone-screen messaging into claims of native mobile/Linux support. |
| Zoom / Google Meet / Teams | **[W]** Advertised meeting workflows. **[V]** Windows system-audio capture exists. **[C]** These are not verified vendor integrations or an exhaustive compatibility certification. |
| Webex / HireVue / phone interviews | **[W]** Mentioned on the website. **[C]** Exact audio routing and platform restrictions require testing. No direct cellular-call capture was established. |
| Slack / Greenhouse / Lever | **[W]** Names appear on the landing page. **[C]** No dedicated integration was verified; do not advertise integration partnerships. |

**[V] Official download destinations found in website source:**

- [Windows direct installer](https://github.com/drBindu/replysis-windows/releases/latest/download/Replysis-win-Setup.exe): HTTP HEAD returned 200.
- [Windows Microsoft Store listing](https://apps.microsoft.com/detail/9N13GQC3MKK9): live listing verified.
- [Windows release page](https://github.com/drBindu/replysis-windows/releases/tag/v1.0.21): version 1.0.21, published September 18, 2026; installer and portable ZIP assets are listed.
- [Currently linked Mac DMG](https://github.com/moto123a/interview-copilot-mac/releases/latest/download/InterviewCopilot-mac.dmg): HTTP HEAD returned 200.
- [Linked Mac release](https://github.com/moto123a/interview-copilot-mac/releases/tag/v1.0.244): version 1.0.244, published August 7, 2026, in a repository archived August 22.

**[C] Release warning:** a downloadable file does not prove current functionality. Confirm the correct Mac repository and replace the marketing destination through a separately approved task if necessary. Store listing availability does not establish its exact installed package version.

## 5. Pricing, credits, trials, and subscription terms

### Published plans

**[V] These values match the inspected project pricing/configuration and public pricing copy. They are not verification of the live Stripe price objects or a completed purchase. Dollar signs are reproduced as published; billing currency and taxes need owner confirmation.**

| Plan | Monthly billing | Annual billing | Monthly credits | Monthly live-listening allowance |
|---|---:|---:|---:|---:|
| Starter | $0 | Not applicable | 100 | 60 minutes |
| Pro | $29.99/month | $299/year; displayed equivalent $24.92/month | 2,000 | 900 minutes / 15 hours |
| Max | $49.99/month | $499/year; displayed equivalent $41.58/month | 5,000 | 1,800 minutes / 30 hours |

**[V] One-time packs:** 500 credits for $9.99; 1,500 for $24.99; 5,000 for $69.99. The checkout code uses one-time payment mode for packs and subscription mode for Pro/Max.

**[W] Published top-up terms:** purchased credits survive monthly refreshes and are used after monthly credits. No subscription is created by a pack purchase.

**[C] Important:** the Java deduction path does not decrement `purchasedCredits` the same way the website route does. Cross-client top-up consumption/reset behavior needs correction or verification before advertising seamless credit handling. No additional listening hours are defined for these packs; do not sell them as listening-time extensions.

### Action costs

**[V] Project-defined/published costs:**

| Action | Credits | Qualification |
|---|---:|---|
| Start live transcription | 1 | Website token-route price; do not assume identical desktop start charging. |
| Generate a live answer | 5 | Per answer, despite an internal legacy name referring to “per minute.” |
| Screen-analysis answer | 5 | Backend answer-cost path; missing as a separate row in the public action list. |
| Generate a question set | 5 | Not five per question in the set. |
| Generate mock feedback | 5 | Per feedback request. |
| Generate a mock example/script | 5 | Separate action. |
| Analyze a resume | 5 | Backend route. |
| Tailor a resume | 20 | Backend route. |
| Verify resume context | 0 | Defined free action. |
| Rewrite a bullet / generate summary | 5 advertised | Do not promote until the request-mode mismatch is resolved. |

**[V] Two separate limits:** credits meter AI actions; listening minutes meter active audio transcription. Turning off microphone input while continuing to transcribe computer audio does not make listening time free.

**[V] Account free tier:** 100 monthly credits with a 60-minute monthly listening allowance.

**[V] Desktop guest configuration:** 100 credits per device per monthly cycle and a separate 30-minute listening allowance. **[W]** The Store discloses the guest credits but not that 30-minute limit.

**[W] Free-start conditions:** no payment card required. **[C]** No fixed-duration “7-day Pro trial,” “14-day trial,” or free unlimited access was established. Describe it as a limited free tier/guest allowance, not a time-limited premium trial.

### Subscription terms and cautions

- **[V]** Pro and Max checkout paths create recurring subscriptions. Annual billing is a yearly payment, not monthly installments.
- **[W]** Users may cancel; paid access continues to the end of the current billing period.
- **[V]** Account/Stripe portal implementation exists. **[C]** Live portal configuration and successful cancellation were not tested.
- **[W]** Monthly included credits do not roll over. The Terms fail to distinguish those from the purchased credits that pricing says do carry over.
- **[W]** Refunds are discretionary/case-by-case, not a money-back guarantee.
- **[V]** Lifetime and Teams remain in legacy configuration but are not offered as active plans by the inspected checkout. Do not advertise them.
- **[C]** Currency, taxes, regional prices, exact reset dates, proration, pack expiry after cancellation, and all live Stripe amounts need confirmation.
- **[C]** Do not advertise “6/100/250 mock sessions per month” as guaranteed. Mock sessions are charged by actions and question count. Five feedback requests plus a generated set already cost 30 credits before any speech-start charges; longer sessions cost more.
- **[C]** Max has 2.5 times Pro’s credits but only twice its listening allowance. Avoid the blanket claim “2.5× everything.”

Source: [current pricing](https://replysis.com/pricing), [Terms](https://replysis.com/terms), and local pricing/billing files listed in Section 16.

## 6. Claims suitable for marketing

**[R] These formulations are supported by inspected implementation, with the stated scope:**

- “One workspace for resume preparation, mock interviews, and live AI suggestions.”
- “Use your resume and job description as context for interview practice.”
- “Practice role-aware questions and review AI coaching feedback.”
- “Compare your resume wording with a target job description.”
- “See suggested answers stream as they are generated.”
- “Read visible questions and coding problems with the Windows desktop app.”
- “Choose a main window or compact overlay on Windows.”
- “Start with 100 monthly credits on the free account plan.”
- “Windows download available directly and through Microsoft Store.”
- “Review AI suggestions and answer in your own words.”

**[R] Responsible-use sentence for demos and detailed descriptions:** “AI suggestions can be inaccurate. Verify your facts and use live assistance only where the interview rules permit it.”

**[C] Before paid promotion:** demonstrate the exact advertised workflow on a normal non-owner account. Internal/unlimited accounts can hide customer-facing billing and entitlement failures.

## 7. Claims to avoid

**[R] Do not use these claims unless the missing evidence or product issue is resolved:**

- “Guaranteed job offer,” “ace every interview,” “the AI that gets you hired,” or numerical success improvements.
- “100% accurate,” “perfect answers,” or “sounds exactly like you.”
- “Undetectable,” “invisible to every screen share,” “proctor-proof,” or “employers can never know.”
- “Always under two seconds,” “instant,” or the mock model card’s approximately 0.15-second speed as a customer guarantee.
- “No data leaves your device,” “offline AI,” “zero data retention,” or “nothing is ever recorded.”
- “All data is always encrypted,” “end-to-end encrypted,” or guaranteed deletion at precisely 90 seconds/seven days.
- “SOC 2 certified,” “GDPR certified,” “HIPAA compliant,” “independently audited,” or a contractual uptime SLA without evidence.
- “Works with every meeting app,” “official Zoom/Teams integration,” or Greenhouse/Lever partnership claims.
- “Identical Windows and Mac apps,” current Mac notarization guarantees, or “no security warnings” without checking the actual installer.
- “Unlimited credits,” “unlimited interviews,” guaranteed monthly mock-session counts, or packs that extend listening time.
- “100% ATS-safe,” guaranteed ATS passage, or employer/recruiter-approved templates.
- Customer counts, ratings, testimonials, employer-logo endorsements, hiring outcomes, or certification badges without verifiable permission and evidence.
- The proposed Interview/Practice switch as an available feature.
- Fixed support-response promises until operationally confirmed.

**[W] Current context:** the Trust page explicitly disclaims unverified certifications, independent audit claims, and a 99.9% contractual SLA. The Proof page showed no published verified reviews during inspection.

**[C] Legal/operational review:** confirm recording/transcription consent requirements, employer/platform rules, legal business identity, and advertising claims with an appropriate adviser. This brief does not certify legal compliance.

## 8. Existing brand and assets

### Colors and typography

**[V] Website design tokens** — source: [globals.css](C:/Users/krish/Desktop/uiii/frontend/app/globals.css).

| Purpose | Existing value |
|---|---|
| Main paper background | #FDFCFA |
| Soft paper | #F7F6F1 |
| Recessed paper | #F0EFE9 |
| Main ink | #16150F |
| Strong / body / muted ink | #2A2A22 / #4A4A41 / #78776C |
| Hairline / strong border | #E9E7E0 / #DAD8CF |
| Primary green | #21924A |
| Deep green | #14532B |
| Secondary green | #2E8B45 |
| Green tint | #EEF7EF |
| Additional CTA green used in components | #1C7A3E |

**[V] Windows tokens** — source: [App.xaml](C:/Users/krish/Desktop/windowsNative/App.xaml): canvas #070B14; surface #0D1422; raised surface #121C2D; text #F4F7FB; muted text #93A4BA; accent #34E08A. Glass-button fills use translucent #EBF2FF-family colors whose alpha follows the existing opacity preference.

**[V] Fonts:** website uses Inter for interface/body text and Fraunces for selected marketing display headings, with system fallbacks. Windows uses Segoe UI in shared controls. The local Mac source references SF Pro Display and SF Mono/Menlo fallbacks. These are font declarations, not evidence that font files are owned or bundled for redistribution.

### Logo and icon files

**[V] Preferred current mark:** the green corner-bracket/star symbol on a dark circular field. The website and Windows 1024-pixel icon files have identical SHA-256 hashes.

Primary files:

- [Website 1024 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-1024.png)
- [Website 512 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-512.png)
- [Website 256 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-256.png)
- [Website 192 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-192.png)
- [Website 180 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-180.png)
- [Website 128 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-128.png)
- [Website 64 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-64.png)
- [Website 32 icon](C:/Users/krish/Desktop/uiii/frontend/public/brand/replysis-icon-32.png)
- [Website application icon](C:/Users/krish/Desktop/uiii/frontend/app/icon.png)
- [Website Apple icon](C:/Users/krish/Desktop/uiii/frontend/app/apple-icon.png)
- [Windows matching 1024 icon](C:/Users/krish/Desktop/windowsNative/Assets/replysis-icon-1024.png)
- [Windows ICO](C:/Users/krish/Desktop/windowsNative/Assets/replysis.ico)

Other existing variants—not automatically approved masters:

- [Green logo variant](C:/Users/krish/Desktop/windowsNative/Assets/replysis-logo-green-1024.png)
- [Older cyan logo](C:/Users/krish/Desktop/windowsNative/Assets/replysis-logo.png)
- [Logo preview](C:/Users/krish/Desktop/windowsNative/Assets/replysis-logo-preview.png)
- [Premium preview](C:/Users/krish/Desktop/windowsNative/Assets/replysis-logo-premium-preview.png)
- [Centered premium preview](C:/Users/krish/Desktop/windowsNative/Assets/replysis-logo-premium-centered-preview.png)

**[V] Store upload artwork:**

- [300px app tile](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/Replysis-AppTile-300x300.png)
- [150px icon](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/Replysis-Icon-150x150.png)
- [71px icon](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/Replysis-Icon-71x71.png)
- [1080px box art](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/Replysis-BoxArt-1080x1080.png)
- [2160px box art](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/Replysis-BoxArt-2160x2160.png)
- [Upload map](C:/Users/krish/Desktop/Replysis-Microsoft-Store-Assets/UPLOAD-MAP.txt)

**[V] Packaged Windows image families:** exact directory [InterviewCopilotPackager/Images](C:/Users/krish/Desktop/windowsNative/InterviewCopilotPackager/Images). It contains StoreLogo, Square44x44Logo, Square150x150Logo, SmallTile, LargeTile, Wide310x150Logo, SplashScreen, and LockScreenLogo scale/target-size variants. These are installer/OS assets, not product screenshots.

**[C] Mac brand cleanup:** the inspected local Mac assets include [avalonia-logo.ico](C:/Users/krish/Desktop/InterviewCopilotMac6/InterviewCopilotMac6/Assets/avalonia-logo.ico), and its plist still uses Interview Copilot naming. Do not use these as the current Replysis social-media identity.

### Screenshots and video

**[V] Existing local visual previews—not fresh authenticated product screenshots:**

- [Main-window preview](C:/Users/krish/AppData/Local/Temp/replysis-design-directions/main-silver-min-width.png)
- [Alternate main preview](C:/Users/krish/AppData/Local/Temp/replysis-design-directions/main-silver.png)
- [Compact-window preview](C:/Users/krish/AppData/Local/Temp/replysis-screen-style-81c710f1c0e544749fb5c9878a832eca/compact-460.png)

**[C] These are temporary-directory design renders from earlier styling work. Do not present them as evidence of live AI performance. A durable, approved screenshot library was not found in the current website public assets.**

**[V] Microsoft Store has two published screenshot assets:**

- [Store screenshot 1](https://store-images.s-microsoft.com/image/apps.13183.13913320314007396.6f9a11a8-c9e0-443a-a327-b8ccdfc36326.77b0f859-b83d-47bd-b0b1-31bfe78983bb?h=480)
- [Store screenshot 2](https://store-images.s-microsoft.com/image/apps.23738.13913320314007396.6f9a11a8-c9e0-443a-a327-b8ccdfc36326.4993ecfb-6d5d-4dad-b930-802d4a55a3f8?h=480)

**[V] The first Store screenshot visibly shows the older blue New Session button and older toolbar. No exact corresponding local screenshot file was located.**

**[V] Existing media files:**

- [Windows intro video](C:/Users/krish/Desktop/windowsNative/Assets/intro.mp4)
- [Website voice-waveform video](C:/Users/krish/Desktop/uiii/frontend/public/company/videos/voice-waveform.mp4)
- [Website sphere video](C:/Users/krish/Desktop/uiii/frontend/public/company/videos/sphere-orbit.mp4)
- [Website geometric-shapes video](C:/Users/krish/Desktop/uiii/frontend/public/company/videos/geometric-shapes.mp4)

**[C] These media files were located, not cleared for campaign usage. Confirm contents, rights, and whether they depict the released product. No editable master logo/vector wordmark was established.**

## 9. Recommended social-media identity

| Field | Recommendation / evidence |
|---|---|
| Page name | **[R]** Replysis |
| Display name | **[R]** Replysis AI |
| Primary username | **[R]** @replysis |
| Backup username 1 | **[R]** @replysisai |
| Backup username 2 | **[R]** @getreplysis |
| Tagline | **[R]** Your experience. Clearer answers. |
| Website | **[V]** [replysis.com](https://replysis.com/) |
| Public contact | **[W]** support@replysis.com, published on the website |
| Business category | **[R]** Software / Software Company; use the closest available platform category |
| Publisher attribution | **[W]** Microsoft Store identifies Varoxel as publisher |

**[C] Username availability, ownership of existing social accounts, mailbox monitoring, and the exact legal relationship between Replysis and Varoxel must be confirmed. No handles were reserved or accounts changed. The Store lists admin@varoxel.com for support, unlike the website.**

**[R] Tone:** calm, useful, product-led, and specific. Show real workflows instead of “secret advantage” messaging. Use the green mark consistently; do not mix the old cyan logo into the launch.

## 10. Ready-to-paste platform bios

**[R] All copy in this section is proposed marketing language. It intentionally avoids unverified speed, compatibility, privacy, and outcome guarantees. Put the website in each platform’s website field where available.**

### Instagram

AI interview prep, grounded in your experience.  
Resume tools • Mock practice • Live suggestions  
Explore Replysis ↓

### Facebook

Replysis brings resume preparation, mock interview practice, and live AI suggestions into one workspace. Add your experience, practice role-aware questions, and review suggestions in your own words. Explore the tools and current plans at replysis.com.

### Threads

Your experience. Clearer answers. AI-assisted resume prep, mock interviews, and live suggestions. Explore Replysis ↓

### LinkedIn

Replysis is an AI-assisted interview workspace for adult job seekers. It connects resume preparation, role-aware mock practice, and live answer suggestions grounded in the context users provide.

Our Windows desktop app adds system-audio capture, a compact overlay, and screen-reading assistance. We share practical walkthroughs, interview preparation tips, and honest explanations of product limits.

AI output requires review. Use live assistance only where interview rules permit it.

Explore the product: https://replysis.com  
Contact: support@replysis.com

### TikTok

Resume prep. Mock interviews. Clearer answers. Meet Replysis.

### YouTube

Prepare your story. Practice your answers. Learn how Replysis works.

This channel covers resume tools, role-aware mock interviews, AI coaching feedback, and the Replysis Windows desktop workflow. Expect practical demos and clear explanations of features, pricing, and limitations.

AI suggestions can be inaccurate. Verify your facts and follow interview rules.

Explore Replysis: https://replysis.com  
Support: support@replysis.com

### X

AI-assisted resume prep, mock interviews, and live suggestions grounded in your experience. Your experience. Clearer answers.

**[C] Before publishing:** check each platform’s current field limits and available categories in its editor. These are deliberately short drafts, not a claim that profile setup or verification is complete.

## 11. Profile picture and banner concepts

**[R] Profile picture:** use the existing 1024px green bracket/star icon, centered on its dark field. Preserve the current symbol and proportions. Keep clear space around it for circular cropping; do not add tiny wording, certification seals, or a “verified” tick.

**[R] Banner:** warm paper background with dark ink typography and one green accent. Headline: “Your experience. Clearer answers.” Subline: “Resume preparation • Mock practice • Live AI suggestions.” Add one genuine, approved Windows screenshot with a discreet glass frame and the website.

**[R] Art direction:** use Inter for supporting text and Fraunces selectively for the headline. Keep social graphics aligned with the website palette; let screenshots retain the app’s dark glass appearance.

**[C] Production conditions:** capture a real released build using clearly labeled fictional demo data. Remove personal information, interview recordings, private documents, and customer identifiers. Do not fabricate response speed through unlabeled jump cuts.

## 12. Launch content ideas

### Ten launch-post ideas — [R]

1. **Meet Replysis:** explain the three workflows in a simple carousel. CTA: explore the website.
2. **From experience to interview context:** show how a factual resume becomes useful context. CTA: prepare your own examples.
3. **A better behavioral outline:** demonstrate Situation, Task, Action, Result without inventing metrics. CTA: save the checklist.
4. **One mock question, one improvement:** show feedback as coaching, not an employer score. CTA: try a practice question.
5. **Main window versus compact:** show the current Windows UI. CTA: explore the Windows download.
6. **What Read screen does:** demonstrate a fictional coding problem. CTA: watch the full walkthrough.
7. **Credits versus listening time:** explain the two allowances and link current pricing.
8. **Before your next call:** cover audio permissions, mic input, setup testing, and interview rules.
9. **Where your data goes:** explain providers, screenshots, and optional history; link the Privacy page without absolute claims.
10. **Build with us:** ask which workflow users want explained next. Do not imply existing customer volume.

### Five short-video/Reel ideas — [R]

1. **“Your resume is context, not a script.”** 20–30 seconds: add fictional experience, show a question, explain how to review the suggestion.
2. **“Practice this before your next interview.”** 20–30 seconds: one question, one response, one feedback takeaway.
3. **“The compact Windows workflow.”** 15–20 seconds: main window to compact overlay; no claims of universal invisibility.
4. **“Two limits, explained.”** 20 seconds: credits count actions; listening allowance counts transcription time.
5. **“Read the problem, then reason.”** 25–40 seconds: screen-reading demo, generated explanation, and a reminder to verify the solution.

**[R] Production rule:** use genuine recordings from a checked build. Label staged examples as demos. Keep captions readable, add subtitles, and do not splice in fake results.

## 13. Three pinned launch posts

**[R] Proposed captions below. Publish only after the demonstrated workflows pass a normal-account smoke test.**

### Pinned post 1 — What Replysis is

**Visual:** existing logo plus three panels: Resume / Practice / Live suggestions.

**Caption:**

Meet Replysis: one workspace for preparing your resume, practicing interview questions, and reviewing live AI suggestions.

Add your experience and target-job context. Practice how you explain your work. Use suggestions as an outline—not a replacement for your judgment.

Your experience. Clearer answers.

Explore Replysis: https://replysis.com

AI output can be inaccurate. Verify facts and use live assistance only where interview rules permit it.

**CTA:** Explore the product.

### Pinned post 2 — How to start

**Visual:** a real, labeled demo showing context setup and mock practice.

**Caption:**

Start with your story.

1. Add your resume and target-role context.
2. Generate a practice question set.
3. Review the feedback and try your answer again.

Focus on examples you can explain honestly: what happened, what you did, and what changed.

Explore mock practice: https://replysis.com/mock-interview

AI coaching scores are guidance—not employer assessments.

**CTA:** Try a practice session.

### Pinned post 3 — Understand the limits before you start

**Visual:** two simple labels: Credits / Listening time.

**Caption:**

Clear limits matter.

Replysis’s free account plan includes 100 credits and one hour of live listening each month. Credits are used for AI actions; listening time is a separate allowance.

Different actions use different amounts, and longer practice sessions use more. Check the current pricing page before choosing a plan.

Plans and usage details: https://replysis.com/pricing

Privacy and data handling: https://replysis.com/privacy

**CTA:** Read the plans and choose what fits your needs.

## 14. Realistic 30-day posting plan

**[R] Suggested workload:** approximately three core posts per week, including one short video. Adapt those assets across platforms; do not create seven unrelated daily campaigns. Use Stories/Threads/X for lighter updates. The days below are a proposed sequence, not scheduled actions.

| Day | Activity |
|---|---|
| 1 | Confirm claims, links, legal identity, and normal-account demo results. No post if critical claims remain unresolved. |
| 2 | Publish pinned post 1: Meet Replysis. Adapt for LinkedIn and Facebook. |
| 3 | Story/Threads question: “Which part of interview preparation is hardest?” |
| 4 | Publish Reel 1: resume context, using fictional demo data. |
| 5 | Reply to comments; record recurring questions for the FAQ. |
| 6 | Publish pinned post 2: how to start practicing. |
| 7 | Review profile visits, link clicks, saves, and meaningful questions. |
| 8 | Prepare the credits/listening explanation; recheck public pricing. |
| 9 | Publish pinned post 3: clear limits. |
| 10 | Story FAQ addressing a genuine question; no invented user quote. |
| 11 | Publish Reel 2: one mock question and feedback takeaway. |
| 12 | Comment/support day; no new feed post required. |
| 13 | Publish a behavioral-answer checklist carousel. |
| 14 | Review results and batch-record the next two demos. |
| 15 | Publish the current Windows main/compact comparison. |
| 16 | Share a short setup tip as a Story or Threads post. |
| 17 | Publish Reel 3: compact workflow. |
| 18 | Answer questions; update the internal content backlog, not product copy. |
| 19 | Publish “What Read screen does” using a fictional example. |
| 20 | Optional short founder note about a verified design decision. |
| 21 | Review which topics produced useful engagement and qualified clicks. |
| 22 | Publish the pre-call setup checklist. |
| 23 | Story: one capability and one limitation, explained together. |
| 24 | Publish Reel 4: credits versus listening time. |
| 25 | Collect voluntary feedback with permission; do not manufacture testimonials. |
| 26 | Publish a plain-language data-flow explainer linking Privacy/Trust. |
| 27 | Answer comments and prepare the next month’s FAQ topics. |
| 28 | Publish a concise roundup of the three core workflows. |
| 29 | Optional Reel 5 if the screen-reading demo is verified; otherwise reuse a tested tutorial. |
| 30 | Review the month and choose the next three content themes. |

**[R] Measurement:** track native platform reach, watch completion, saves, profile visits, and link clicks. Add signup/activation figures only if a verified, consent-compatible measurement method exists. Do not invent conversion rates or promise follower counts.

**[C] Tracking boundary:** the current Cookie Policy says no advertising/retargeting cookies or Meta Pixel. Do not introduce campaign tracking pixels without a separately approved privacy/consent review.

## 15. Inconsistencies and owner-confirmation list

| Issue | Evidence label and finding | Marketing consequence |
|---|---|---|
| Mac download age | **[V]** Live website links to archived Mac repository’s August 7 release. Local Mac source is also differently branded. | **[R]** Do not claim current cross-platform parity. Confirm the correct Mac release destination. |
| Desktop entitlement | **[V]** Website pricing/config says free users do not get desktop access; Windows implements guest access and Store advertises it. | **[R]** Explain free desktop trial versus paid features explicitly. |
| Mock capacity | **[V]** Pricing lists 6/100/250 sessions, but code charges by generated sets, feedback, scripts, and speech starts. | **[R]** Advertise credits/hours, not fixed session counts. |
| Resume AI buttons | **[V]** Rewrite/summary request modes are rejected by the inspected AI route after a separate deduction step. | **[R]** Exclude these specific actions from launch promises until fixed and tested. |
| Purchased-credit accounting | **[V]** Website routes decrement purchased-credit balance; inspected Java deduction does not. Monthly resets add the stored purchased balance back. | **[C]** Confirm deployed behavior and reconcile before promising consistent top-up handling. |
| Credit rollover terms | **[W]** Terms say unused credits do not roll over; pricing says purchased credits survive refreshes. | **[R]** Distinguish included credits from purchased credits in future approved copy. |
| Listening disclosure | **[V]** Guest backend allowance is 30 minutes; Store copy mentions only 100 credits. Website explanatory text describes “microphone on,” although system-audio transcription also consumes listening time. | **[R]** Describe active transcription, not merely microphone input. |
| Screen retention | **[V]** Cache entries expire after 90 seconds but are swept during requests; no independent timed purge was found in that cache implementation. **[W]** Policy promises deletion within 90 seconds. | **[R]** Do not repeat that exact deletion guarantee until implementation/policy align. |
| Audio retention/encryption | **[V]** Windows protects completed WAV files and cleans old recordings when the app runs; plaintext recovery handling exists. **[W]** Policy broadly describes encrypted copies deleted after seven days. | **[R]** Avoid exact unattended deadlines and always-encrypted claims. |
| Audio privacy shorthand | **[W]** Landing/pricing repeatedly say raw audio is not stored. **[V]** Optional Windows local audio saving exists. | **[R]** Scope any claim to application-server storage and disclose local recording separately. |
| Provider disclosures | **[V]** Windows has Deepgram/Speechmatics and a Sarvam path. **[W]** Home copy still emphasizes Speechmatics; Terms omit Deepgram; Privacy does not list Sarvam. | **[C]** Confirm active providers per platform/language and reconcile disclosure. |
| Proposed audio modes | **[V]** Interview/Practice proposal is not established in inspected Windows UI; mic capture still defaults on. | **[R]** Do not announce this as shipped. |
| Store screenshot age | **[V]** Published screenshot 1 shows older blue buttons and toolbar, unlike current Windows source/previews. | **[R]** Use a fresh approved released-build screenshot for launch creatives. |
| Session wording | **[V]** Windows saves interviewer text and generated suggestions; cloud sync labels generated output as candidate content. **[W]** Store says users can review what they said. | **[R]** Say “questions and saved suggestions,” not a guaranteed verbatim record of the candidate’s speech. |
| History deletion | **[V]** Inspected sessions API exposes GET/POST; Windows hides deletion for cloud-only sessions. **[W]** Policy says history remains until deleted. | **[C]** Verify the usable cloud-session deletion route or support process before promoting full self-service control. |
| History/plan gating | **[V]** Windows local session history and guest workflow exist despite paid-history positioning. Dedicated mock persistence was not established. | **[R]** Separate local, in-session, and cloud history. |
| OS requirements | **[W]** Store specifies Windows build 17763+; local packaging declares different minimums. Mac website says 12+/Apple Silicon without fresh installer validation. | **[C]** Establish one tested requirements table. |
| Support identity | **[W]** Website uses support@replysis.com; Store uses admin@varoxel.com and publisher Varoxel. Pricing promises same-day replies; Privacy says two business days. | **[C]** Confirm the public support address and service expectation. |
| Eligibility | **[W]** Terms require 18+; Store content rating is Everyone. These are different concepts. | **[R]** Do not target children or claim all ages may create accounts. |
| ATS/employer wording | **[V]** Template metadata says “100% ATS-safe” and “Google/Meta recruiter format”; local keyword score is a heuristic. | **[R]** Avoid employer endorsement and guaranteed ATS claims. |
| Performance/quality wording | **[W]** Site uses a sub-two-second target, a hiring-outcome headline, and “best” models; UI includes speed badges. | **[R]** Use streaming/AI-assisted descriptions, not measured guarantees or comparative superiority. |
| Policy freshness | **[W]** Privacy/Trust show August 13 and Terms/Cookies May 1 despite later product changes. | **[C]** Confirm effective dates and the promised notice process; do not silently backdate changes. |
| Demo versus evidence | **[V]** Homepage includes staged sample answers/scores and demo components. | **[R]** Label examples; never turn sample metrics into customer outcomes. |
| Account/config reality | **[C]** Production Stripe objects, default non-owner entitlements, current Store package version, Mac signing, and exact deployed commits were not fully verified. | **[R]** Resolve before making precise payment, parity, or release-assurance claims. |

**[R] Recommended launch posture:** begin with honest educational/product walkthrough content after the demonstrated paths are checked. Hold feature-specific paid advertising for broken or unresolved workflows. This brief is not a public-release GO or proof that the entire product is bug-free.

## 16. Evidence locations

**[V] Principal local sources inspected:**

- Windows project root: [windowsNative](C:/Users/krish/Desktop/windowsNative)
- Windows UI/behavior: [MainWindow.xaml.cs](C:/Users/krish/Desktop/windowsNative/MainWindow.xaml.cs), [SettingsWindow.xaml.cs](C:/Users/krish/Desktop/windowsNative/SettingsWindow.xaml.cs), [App.xaml](C:/Users/krish/Desktop/windowsNative/App.xaml)
- Audio and local protection: [speechmatics_engine.py](C:/Users/krish/Desktop/windowsNative/speechmatics_engine.py), [SecureDataProtector.cs](C:/Users/krish/Desktop/windowsNative/SecureDataProtector.cs)
- History: [CloudSessionSync.cs](C:/Users/krish/Desktop/windowsNative/CloudSessionSync.cs), [SessionsPanel.xaml.cs](C:/Users/krish/Desktop/windowsNative/SessionsPanel.xaml.cs)
- Release/requirements: [InterviewCopilot.csproj](C:/Users/krish/Desktop/windowsNative/InterviewCopilot.csproj), [Package.appxmanifest](C:/Users/krish/Desktop/windowsNative/InterviewCopilotPackager/Package.appxmanifest)
- Prior handoff/reference: [MAC_CATCHUP.md](C:/Users/krish/Desktop/windowsNative/MAC_CATCHUP.md), [WINDOWS_INVENTORY.md](C:/Users/krish/Desktop/windowsNative/WINDOWS_INVENTORY.md). Older notes were not treated as current behavior without checking source.
- Website pricing: [pricing/page.tsx](C:/Users/krish/Desktop/uiii/frontend/app/pricing/page.tsx), [productFacts.ts](C:/Users/krish/Desktop/uiii/frontend/data/productFacts.ts), [creditPacks.ts](C:/Users/krish/Desktop/uiii/frontend/data/creditPacks.ts)
- Resume UI/templates: [resume/page.tsx](C:/Users/krish/Desktop/uiii/frontend/app/resume/page.tsx), [templates.tsx](C:/Users/krish/Desktop/uiii/frontend/app/resume/components/templates.tsx)
- Mock practice: [mock-interview/page.tsx](C:/Users/krish/Desktop/uiii/frontend/app/mock-interview/page.tsx)
- AI route: [tokens/route.ts](C:/Users/krish/Desktop/uiii/frontend/app/api/stt/tokens/route.ts)
- Billing: [checkout/route.ts](C:/Users/krish/Desktop/uiii/frontend/app/api/stripe/checkout/route.ts), [webhook/route.ts](C:/Users/krish/Desktop/uiii/frontend/app/api/stripe/webhook/route.ts), [deduct/route.ts](C:/Users/krish/Desktop/uiii/frontend/app/api/credits/deduct/route.ts)
- Backend screen/answer logic: [InterviewController.java](C:/Users/krish/Desktop/uiii/backend/src/main/java/com/replysis/backend/controller/InterviewController.java)
- Backend credit accounting: [FirestoreCreditsService.java](C:/Users/krish/Desktop/uiii/backend/src/main/java/com/replysis/backend/service/FirestoreCreditsService.java)
- Backend resume routes: [ResumeController.java](C:/Users/krish/Desktop/uiii/backend/src/main/java/com/replysis/backend/controller/ResumeController.java)
- Document-service README: [README.md](C:/Users/krish/Desktop/uiii/pdf-service/README.md)
- Website branding/download links: [layout.tsx](C:/Users/krish/Desktop/uiii/frontend/app/layout.tsx), [BrandIcon.tsx](C:/Users/krish/Desktop/uiii/frontend/components/BrandIcon.tsx), [Footer.tsx](C:/Users/krish/Desktop/uiii/frontend/components/Footer.tsx)
- Local Mac project: [InterviewCopilotMac6](C:/Users/krish/Desktop/InterviewCopilotMac6), including [Info.plist](C:/Users/krish/Desktop/InterviewCopilotMac6/InterviewCopilotMac6/Assets/Info.plist) and [appcast.xml](C:/Users/krish/Desktop/InterviewCopilotMac6/appcast.xml).

**[V] Public pages read:** [Home](https://replysis.com/), [Features](https://replysis.com/features), [How it works](https://replysis.com/how-it-works), [Pricing](https://replysis.com/pricing), [Privacy](https://replysis.com/privacy), [Trust](https://replysis.com/trust), [Terms](https://replysis.com/terms), [Cookies](https://replysis.com/cookies), [Proof](https://replysis.com/proof), [Microsoft Store](https://apps.microsoft.com/detail/9N13GQC3MKK9), and the Windows/Mac releases linked above.

**[V] Availability checks:** public pages and website health endpoint returned HTTP 200 during inspection. That establishes reachability, not successful AI generation, billing, transcription, or export.

**[C] Final owner decisions:** confirm the canonical Mac download, actual paid/free entitlements, live Stripe amounts/currency, support identity, approval to use the assets, and resolution of the flagged request/billing/privacy mismatches before broad paid marketing.

