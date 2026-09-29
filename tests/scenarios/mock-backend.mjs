// A fake Replysis server that plays one account situation, so the real app can be run
// against it and checked for what the person actually sees.
//   node mock-backend.mjs <scenario> <port>
// Logs one line per request to stdout: "REQ GET /api/v1/stt/key -> 402".
import http from "node:http";

const scenario = process.argv[2] ?? "healthy-credits";
const port = Number(process.argv[3] ?? 18081);
let keyRequests = 0;

// What each situation looks like from the app's side.
const S = {
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
  if (url === "/api/v1/stt/key") { keyRequests++; [status, body, headers = {}] = s.key(); }
  else if (url === "/api/v1/interview/credits") body = { credits: s.credits, plan: "free", isUnlimited: false };
  else if (url === "/api/v1/usage/listening") body = { remainingMinutes: s.minutes, usedMinutes: 15 - s.minutes };
  else if (url === "/health") body = { ok: true };
  console.log(`REQ ${req.method} ${url} -> ${status}`);
  res.writeHead(status, { "Content-Type": "application/json", ...headers });
  res.end(JSON.stringify(body));
}).listen(port, "127.0.0.1", () => console.log(`READY ${scenario} on ${port}`));
