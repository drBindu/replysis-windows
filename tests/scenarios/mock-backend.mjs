// A fake Replysis server that plays one account situation, so the real app can be run
// against it and checked for what the person actually sees.
//   node mock-backend.mjs <scenario> <port>
// Logs one line per request to stdout: "REQ GET /api/v1/stt/key -> 402".
import http from "node:http";

const scenario = process.argv[2] ?? "healthy-credits";
const port = Number(process.argv[3] ?? 18081);
let keyRequests = 0;
let tokenRequests = 0;

// The token a refresh hands out, and the only one the wake scenarios accept.
const FRESH = "Bearer fresh-token";
const woke = (req) =>
  req.headers.authorization === FRESH
    ? [402, { error: "fair use", reason: "audio-limit" }]   // a real answer, not a sign-in problem
    : [401, { error: "Invalid or missing token" }];

// What each situation looks like from the app's side.
const S = {
  // A laptop that slept: the sign-in it holds is stale. The app must refresh it and ask again,
  // not tell the person to sign in. (2026-09-29: it did, and never recovered.)
  "wake-token-rejected": { credits: 55, minutes: 0, key: woke },   // app believes the token is fine, the server does not
  "wake-token-expired":  { credits: 55, minutes: 0, key: woke },   // app knows it is old and refreshes first
  // The server refuses speech with the reason the real one gives.
  "no-listening": { credits: 55, minutes: 0, key: () => [402, { error: "fair use", reason: "audio-limit" }] },
  "no-credits":   { credits: 0,  minutes: 30, key: () => [402, { error: "No credits remaining" }] },
  "signed-out":   { credits: 55, minutes: 30, key: () => [401, { error: "Invalid or missing token" }] },
  "service-down": { credits: 55, minutes: 30, key: () => [503, { error: "Speech service not configured on server" }] },
  "provider-busy": { credits: 55, minutes: 30, key: () => [502, { error: "Speechmatics rejected the mint" }] },
  // A rate limit with no known reason behind it: only a passing reconnect.
  "rate-limited": { credits: 55, minutes: 30, key: () => [429, { error: "Too many token requests." }, { "Retry-After": "60" }] },
  // Refused once, then rate limited: what really happened to a tester on 2026-09-29.
  "refused-then-rate-limited": { credits: 55, minutes: 0,
    key: () => keyRequests === 1 ? [402, { error: "fair use", reason: "audio-limit" }] : [429, { error: "Too many" }, { "Retry-After": "15" }] },
};
const s = S[scenario];
if (!s) { console.error("unknown scenario", scenario, Object.keys(S)); process.exit(2); }

http.createServer((req, res) => {
  const url = req.url.split("?")[0];
  let status = 200, body = {}, headers = {};
  if (url === "/token") { tokenRequests++; body = { id_token: "fresh-token", refresh_token: "refresh-2", expires_in: "3600" }; }
  else if (url === "/api/v1/stt/key") { keyRequests++; [status, body, headers = {}] = s.key(req); }
  else if (url === "/api/v1/interview/credits") body = { credits: s.credits, plan: "free", isUnlimited: false };
  else if (url === "/api/v1/usage/listening") body = { remainingMinutes: s.minutes, usedMinutes: 15 - s.minutes };
  else if (url === "/health") body = { ok: true };
  console.log(`REQ ${req.method} ${url} -> ${status}${url === "/api/v1/stt/key" ? " auth=" + (req.headers.authorization === FRESH ? "fresh" : "stale/none") : ""}`);
  res.writeHead(status, { "Content-Type": "application/json", ...headers });
  res.end(JSON.stringify(body));
}).listen(port, "127.0.0.1", () => console.log(`READY ${scenario} on ${port}`));
