// Runs the REAL app (a Debug build) against a fake server for each account situation and
// checks what the person is told. This is the test that was missing on 2026-09-29: the code
// and the unit tests were green while a Free account met a silent, unexplained app.
//
//   dotnet build InterviewCopilot.csproj -c Debug
//   node tests/scenarios/run-scenarios.mjs            # all of them
//   node tests/scenarios/run-scenarios.mjs no-listening
//
// It needs a saved sign in (the app must have been opened and logged in once on this PC).
// Pictures of each screen are written to tests/scenarios/out/ so they can be looked at.
import { spawn, execFileSync } from "node:child_process";
import { readFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import os from "node:os";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "..", "..");
const exe = resolve(root, "bin/Debug/net8.0-windows10.0.19041.0/InterviewCopilot.exe");
const logPath = resolve(process.env.LOCALAPPDATA ?? os.homedir(), "InterviewCopilot", "debug.log");
const outDir = resolve(here, "out");
mkdirSync(outDir, { recursive: true });
if (!existsSync(exe)) { console.error("Build the Debug app first:\n  dotnet build InterviewCopilot.csproj -c Debug"); process.exit(2); }

// What each situation must show, and must not do.
//   env         extra environment for the app
//   startMockMs when the fake server starts (default: before the app); later = "the network was not up yet"
//   waitMs      how long to let the app run before judging
//   forbid      problems that must NEVER be shown
//   tokenCalls / keyCalls   exact number of token refreshes / speech key requests expected
const TOKEN_PORT = (i) => 18100 + i;
const CASES = [
  { name: "no-listening",  problem: "NoListeningTime",    banner: true,  words: ["this month's limit", "answers are safe", "F8"] },
  { name: "no-credits",    problem: "NoCredits",          banner: true,  words: ["used up", "upgrade"] },
  { name: "signed-out",    problem: "SignInExpired",      banner: true,  words: ["sign in", "Sign out"] },
  { name: "service-down",  problem: "ServiceUnavailable", banner: true,  words: ["temporarily unavailable", "nothing needs to be done"] },
  { name: "rate-limited",  problem: "WaitingToReconnect", banner: false, words: [] },
  // A laptop wakes from sleep holding a stale sign-in. It must refresh and ask again, and end up
  // at the real answer (no listening time), never at "sign in again".
  { name: "wake-token-rejected", problem: "NoListeningTime", banner: true, words: ["this month's limit"],
    env: { REPLYSIS_STALE_TOKEN: "rejected" }, forbid: ["SignInExpired"], tokenCalls: 1, keyCalls: 2 },
  { name: "wake-token-expired", problem: "NoListeningTime", banner: true, words: ["this month's limit"],
    env: { REPLYSIS_STALE_TOKEN: "expired" }, forbid: ["SignInExpired"], tokenCalls: 1, keyCalls: 1 },
  // ... and the network is not up yet when the app first asks. It must keep asking, in seconds,
  // and reach the real answer once the server can be reached.
  { name: "wake-network-late", scenario: "no-listening", problem: "NoListeningTime", banner: true, words: ["this month's limit"],
    startMockMs: 25_000, waitMs: 60_000, forbid: ["SignInExpired"] },
];
const only = process.argv[2];
const cases = only ? CASES.filter((c) => c.name === only) : CASES;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const port = (i) => 18100 + i;
let failures = 0;
const bad = (m) => { failures++; console.log(`     FAIL  ${m}`); };
const ok = (m) => console.log(`     ok    ${m}`);

function snapshot(pid, file) {
  // The app window, drawn by Windows itself, so nothing else on the screen is captured.
  const ps = `
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class Wn{[DllImport("user32.dll")]public static extern bool SetProcessDPIAware();[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out R r);[DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr d,uint f);[StructLayout(LayoutKind.Sequential)]public struct R{public int L,T,Rt,B;}}'
[void][Wn]::SetProcessDPIAware()
$p=Get-Process -Id ${pid}; $r=New-Object Wn+R; [void][Wn]::GetWindowRect($p.MainWindowHandle,[ref]$r)
$b=New-Object System.Drawing.Bitmap ($r.Rt-$r.L),($r.B-$r.T); $g=[System.Drawing.Graphics]::FromImage($b); $h=$g.GetHdc()
[void][Wn]::PrintWindow($p.MainWindowHandle,$h,2); $g.ReleaseHdc($h); $b.Save('${file.replace(/\\/g, "\\\\")}')`;
  try { execFileSync("powershell", ["-NoProfile", "-Command", ps], { stdio: "ignore" }); } catch { /* the picture is a bonus */ }
}

for (const [i, c] of cases.entries()) {
  console.log(`\n${c.name}`);
  const waitMs = c.waitMs ?? 28_000;
  let mock = null;
  let requests = "";
  const startMock = () => {
    mock = spawn("node", [resolve(here, "mock-backend.mjs"), c.scenario ?? c.name, String(port(i))], { stdio: ["ignore", "pipe", "inherit"] });
    mock.stdout.on("data", (d) => (requests += d));
  };
  // Most scenarios have the server up first. "The network was not up yet" starts it late.
  if (!c.startMockMs) { startMock(); await sleep(700); }

  // The app starts a fresh debug.log on every launch (the old one becomes debug.prev.log),
  // so everything in it after this point belongs to this run.
  // REPLYSIS_TOKEN_URL sends sign-in refreshes to the fake server too, and keeps the fake
  // tokens away from the real saved sign-in.
  const app = spawn(exe, [], {
    cwd: resolve(exe, ".."), stdio: "ignore",
    env: {
      ...process.env,
      REPLYSIS_BACKEND_URL: `http://127.0.0.1:${port(i)}`,
      REPLYSIS_TOKEN_URL: `http://127.0.0.1:${port(i)}/token`,
      ...(c.env ?? {}),
    },
  });
  if (c.startMockMs) { await sleep(c.startMockMs); startMock(); await sleep(waitMs - c.startMockMs); }
  else await sleep(waitMs);
  snapshot(app.pid, resolve(outDir, `${c.name}.png`));
  const log = readFileSync(logPath, "utf8");
  try { execFileSync("taskkill", ["/PID", String(app.pid), "/T", "/F"], { stdio: "ignore" }); } catch {}
  mock?.kill();
  await sleep(1500);

  const shown = [...log.matchAll(/Problem shown to the user: (\w+)/g)].map((m) => m[1]);
  const keyCalls = (requests.match(/GET \/api\/v1\/stt\/key/g) ?? []).length;
  const tokenCalls = (requests.match(/POST \/token/g) ?? []).length;

  if (c.banner) (shown.includes(c.problem) ? ok : bad)(`the person is told, in words: ${c.problem} (${shown.join(",") || "nothing shown"})`);
  else (shown.length === 0 ? ok : bad)(`no alarming banner for a passing reconnect (${shown.join(",") || "none"})`);
  (keyCalls >= 1 ? ok : bad)(`the app reached the fake server (${keyCalls} speech key request(s))`);
  (keyCalls <= 3 ? ok : bad)(`it does not hammer the server: ${keyCalls} speech key request(s) in ${Math.round(waitMs / 1000)} seconds`);
  for (const f of c.forbid ?? []) (!shown.includes(f) ? ok : bad)(`never told "${f}" (shown: ${shown.join(",") || "nothing"})`);
  if (c.tokenCalls != null) (tokenCalls === c.tokenCalls ? ok : bad)(`the sign-in was refreshed ${tokenCalls} time(s), expected ${c.tokenCalls}`);
  if (c.keyCalls != null) (keyCalls === c.keyCalls ? ok : bad)(`${keyCalls} speech key request(s), expected ${c.keyCalls}${c.keyCalls === 2 ? " (refused, refreshed, asked again)" : ""}`);
  if (c.startMockMs) {
    const retries = (log.match(/trying again in \d+ s/g) ?? []).length;
    (retries >= 2 ? ok : bad)(`with no server for ${c.startMockMs / 1000} s the app kept asking, in seconds (${retries} tries logged)`);
  }
  // A server that cannot be reached logs its refused connections as exceptions, on purpose. A
  // crash is an unhandled one.
  (!/Unhandled|crash|FATAL/i.test(log) ? ok : bad)("no crash or unhandled error in the log");
  console.log(`     picture: tests/scenarios/out/${c.name}.png`);
}

writeFileSync(resolve(outDir, "last-run.txt"), `${new Date().toISOString()}  ${failures === 0 ? "ALL PASSED" : failures + " FAILED"}\n`);
console.log(`\n${failures === 0 ? "ALL SCENARIOS PASSED" : failures + " CHECK(S) FAILED"}`);
process.exit(failures === 0 ? 0 : 1);
