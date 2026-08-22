import sys  # bare exit() is only defined when the site module loads; under a
# PyInstaller build it is not, so every exit() below must be sys.exit().
import os
import json
import argparse
import asyncio
import base64
import wave
import threading
import tempfile
import struct
import time
import urllib.request
import urllib.error
from collections import deque

# A PyInstaller build gets stdout on the legacy Windows code page rather than
# UTF-8. Printing any non-ASCII character then raised UnicodeEncodeError, which
# the connection loop caught as a transport failure and retried forever, so the
# app sat on "connecting" and never came online. Force UTF-8 both ways; the C#
# side reads these pipes as UTF-8 to match.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# PyAudioWPatch supports WASAPI loopback (as_loopback=True); fall back to stock pyaudio.
try:
    import pyaudiowpatch as pyaudio
    _WPATCH = True
except ImportError:
    import pyaudio
    _WPATCH = False

# ── MIC TEST ──────────────────────────────────────────────────────────────────
def test_microphone():
    print("", flush=True)
    print("=" * 50, flush=True)
    print("   TESTING MICROPHONE...", flush=True)
    print("=" * 50, flush=True)

    try:
        p_test = pyaudio.PyAudio()
        num_devices = p_test.get_host_api_info_by_index(0).get('deviceCount', 0)
        print(f">>> Audio devices found: {num_devices}", flush=True)

        print(">>> Available INPUT devices:", flush=True)
        for i in range(p_test.get_device_count()):
            info = p_test.get_device_info_by_index(i)
            if info.get('maxInputChannels', 0) > 0:
                print(f"    [{i}] {info['name']} (rate={int(info['defaultSampleRate'])})", flush=True)

        default_input = p_test.get_default_input_device_info()
        print(f">>> Default input: {default_input['name']} (index {default_input['index']})", flush=True)

        # Try 16kHz mono first; fall back to device native rate if unsupported
        test_stream = None
        test_chunk = 4096
        try:
            test_stream = p_test.open(
                format=pyaudio.paInt16, channels=1, rate=16000,
                input=True, frames_per_buffer=test_chunk
            )
            print(">>> MIC test opened at 16kHz mono", flush=True)
        except Exception as ex16:
            print(f">>> MIC 16kHz open failed ({ex16}) — trying native rate...", flush=True)
            native_rate = int(default_input.get('defaultSampleRate', 44100))
            native_ch   = max(1, min(int(default_input.get('maxInputChannels', 1)), 2))
            test_chunk  = max(4096, int(4096 * native_rate / 16000))
            test_stream = p_test.open(
                format=pyaudio.paInt16, channels=native_ch, rate=native_rate,
                input=True, frames_per_buffer=test_chunk
            )
            print(f">>> MIC test opened at {native_rate}Hz {native_ch}ch", flush=True)

        # 3 reads (not 10) is enough to prove the device truly opens and streams
        # without error — this is a hardware sanity check, not a real listening
        # window, so there's no reason to burn ~1.8 extra seconds of pure startup
        # latency waiting for more chunks than needed to catch a broken device.
        has_audio = False
        for _ in range(3):
            data = test_stream.read(test_chunk, exception_on_overflow=False)
            max_val = max(
                abs(int.from_bytes(data[j:j+2], byteorder='little', signed=True))
                for j in range(0, min(len(data), 200), 2)
            )
            if max_val > 500:
                has_audio = True

        test_stream.stop_stream()
        test_stream.close()
        p_test.terminate()

        status = "Audio signal detected!" if has_audio else "Silent - hardware OK"
        print(f">>> MIC TEST: PASSED - {status}", flush=True)
        print("=" * 50, flush=True)
        return True

    except Exception as e:
        print(f">>> MIC TEST: FAILED - {e}", flush=True)
        print("    Fix: Windows Settings > Privacy > Microphone - allow app access", flush=True)
        print("=" * 50, flush=True)
        return False


def find_vbcable_device(p):
    """Find VB-Cable virtual audio device index automatically."""
    for i in range(p.get_device_count()):
        info = p.get_device_info_by_index(i)
        name = info['name'].lower()
        if info.get('maxInputChannels', 0) > 0:
            if 'cable output' in name or 'vb-audio' in name or 'vb cable' in name:
                print(f">>> VB-Cable found: [{i}] {info['name']}", flush=True)
                return i
    print(">>> VB-Cable NOT found.", flush=True)
    return None


def _signal_level(data: bytes) -> int:
    """Return max absolute amplitude from a PCM S16LE buffer."""
    try:
        n = len(data) // 2
        if n == 0:
            return 0
        samples = struct.unpack_from(f'<{n}h', data)
        return max(abs(s) for s in samples)
    except Exception:
        return 0


def _probe_device(p, dev, timeout_sec=0.4):
    """
    Open a loopback device, read 80 ms, and report (responded, amplitude).

    The two facts are separate and only one of them was being returned. A
    device playing nothing and a device that has frozen both measure zero
    amplitude, so a caller seeing zero could not tell "correct device, nothing
    playing yet" from "this device will never return audio again". Selection
    treated them alike, and a frozen device could therefore be chosen at
    startup and then hang the moment real audio was expected, taking system
    audio down with it.

    Which is not one machine's problem. Every Windows PC carries several
    loopback devices, and the ones belonging to virtual cables and capture
    tools accept a stream and then never produce a buffer. Which of them is
    real differs on every user's machine, so the only reliable test is whether
    the device answers.

    Runs in a daemon thread so a hung device cannot block startup past
    timeout_sec.
    """
    result = {'responded': False, 'level': 0}

    def _worker():
        try:
            test_frames = max(1024, int(dev['rate'] * 0.08))   # 80 ms
            ts = p.open(format=pyaudio.paInt16,
                        channels=dev['channels'],
                        rate=dev['rate'],
                        input=True,
                        input_device_index=dev['index'],
                        frames_per_buffer=test_frames)
            data = ts.read(test_frames, exception_on_overflow=False)
            ts.stop_stream()
            ts.close()
            result['level'] = _signal_level(data)
            result['responded'] = True
        except Exception:
            pass

    t = threading.Thread(target=_worker, daemon=True)
    t.start()
    t.join(timeout=timeout_sec)
    return result['responded'], result['level']


def _test_device_signal(p, dev, timeout_sec=0.4) -> int:
    """Amplitude only, for callers that do not care whether the device answered."""
    return _probe_device(p, dev, timeout_sec)[1]


_timeout_warned = set()

def _read_stream_timeout(stream, frames, timeout_sec, label):
    """Blocking-read helper with a hard timeout. PyAudio's stream.read() has no
    native timeout parameter — if a device driver stalls, the call can hang
    indefinitely without ever raising, which would silently freeze the ENTIRE
    producer loop forever (no exception, no further output, connection still
    looks 'online' because that's a separate task). This makes a hang visible
    instead of invisible: runs the read in a daemon thread and gives up after
    timeout_sec, logging once (not every call) so a real hang is unmistakable
    in the log rather than just... nothing happening."""
    result = [None]

    def _worker():
        try:
            result[0] = stream.read(frames, exception_on_overflow=False)
        except Exception as ex:
            result[0] = ("__ERR__", str(ex))

    t = threading.Thread(target=_worker, daemon=True)
    t.start()
    t.join(timeout=timeout_sec)

    if t.is_alive():
        if label not in _timeout_warned:
            print(f">>> {label} READ TIMEOUT after {timeout_sec}s — stream.read() is HANGING "
                  f"(not erroring, just never returning). This is why no audio ever gets through.",
                  flush=True)
            _timeout_warned.add(label)
        return None

    if isinstance(result[0], tuple) and result[0] and result[0][0] == "__ERR__":
        print(f">>> {label} read error: {result[0][1]}", flush=True)
        return None

    _timeout_warned.discard(label)   # recovered — allow the warning to fire again if it hangs later
    return result[0]


def open_stream_with_timeout(p, open_kwargs, timeout_sec=6.0):
    """Open a PortAudio stream without letting one wedged device hang startup.

    A virtual input left in a bad state (an app killed mid-session, driver
    software still loading) can block p.open() indefinitely. That stranded the
    whole engine before it ever reached the transcription socket, so the app
    sat on "connecting" with no way to recover but a restart. Opening on a
    daemon thread means a stuck device is abandoned and the caller falls back,
    which downgrades the session instead of losing it.
    """
    result = {}

    def _worker():
        try:
            result["stream"] = p.open(**open_kwargs)
        except Exception as ex:            # noqa: BLE001 — reported to the caller below
            result["error"] = ex

    worker = threading.Thread(target=_worker, daemon=True)
    worker.start()
    worker.join(timeout_sec)

    if worker.is_alive():
        raise TimeoutError(f"audio device did not open within {timeout_sec:.0f}s")
    if "error" in result:
        raise result["error"]
    return result.get("stream")


def find_wasapi_loopback_device(p):
    """
    Find the WASAPI loopback device that actually carries audio.
    Populates _loopback_candidates (all devices) for hot-swap.
    Returns (device_index, native_rate, native_channels) or None.
    """
    global _loopback_candidates, _active_loopback_index

    if not _WPATCH:
        print(">>> PyAudioWPatch not installed -- WASAPI loopback unavailable.", flush=True)
        print(">>> Run: py -m pip install PyAudioWPatch", flush=True)
        return None

    try:
        # Devices that share the mic's product family (e.g. all "SteelSeries
        # Sonar" virtual channels — Mic/Chat/Media/Aux/Gaming) run through the
        # SAME underlying audio engine as the mic capture. Confirmed by direct
        # testing: opening ANY sibling Sonar channel — even briefly, just to
        # probe it — corrupts the already-open mic stream's routing for the
        # rest of the session (mic goes silent for many seconds). Excluding
        # sibling-family devices from system-audio candidates entirely (both
        # initial selection and hot-swap) avoids ever touching them.
        # ...but only while the mic is actually open. In system-audio-only mode
        # the mic is never opened, so there is no stream to corrupt and no reason
        # to exclude anything.
        #
        # That distinction is what made Interview Auto unusable on machines where
        # the headset supplies both the microphone and the speakers, which is the
        # normal arrangement for Sonar, Voicemeeter and similar. The exclusion
        # removed the user's real output device, selection fell through to an
        # unrelated one carrying no audio, that device then hung, and system
        # audio was switched off for the session. Interview Auto listens to
        # system audio with the mic off, so the result was total silence with
        # nothing on screen to explain it.
        # Families confirmed to behave this way, by name fragment. Kept as a list
        # rather than derived from the mic's name because the failure is a
        # property of a particular driver, not of two devices happening to share
        # a brand: excluding every same-brand loopback by default would remove
        # working system audio from users whose hardware is fine, to prevent a
        # problem their hardware does not have.
        SHARED_ENGINE_FAMILIES = ("sonar",)

        mic_lower  = _mic_device_name.lower()
        mic_family = ""
        if args.mode == "both":
            for family in SHARED_ENGINE_FAMILIES:
                if family in mic_lower:
                    mic_family = family
                    break

        # Collect all loopback candidates
        candidates = []
        for lb in p.get_loopback_device_info_generator():
            if mic_family and mic_family in lb['name'].lower():
                print(f">>> Loopback candidate SKIPPED (shares mic's audio engine): [{lb['index']}] {lb['name']}", flush=True)
                continue
            rate     = int(lb['defaultSampleRate'])
            channels = max(1, lb.get('maxInputChannels', 2))
            candidates.append({'index': lb['index'], 'name': lb['name'],
                                'rate': rate, 'channels': channels})
            print(f">>> Loopback candidate: [{lb['index']}] {lb['name']} ({rate}Hz {channels}ch)", flush=True)

        if not candidates:
            print(">>> No WASAPI loopback devices found (all candidates shared the mic's audio engine).", flush=True)
            return None

        _loopback_candidates = candidates  # store for hot-swap

        # Default output device name (fallback if all signal tests fail)
        wasapi_info  = p.get_host_api_info_by_type(pyaudio.paWASAPI)
        default_out  = wasapi_info.get('defaultOutputDevice', -1)
        default_name = p.get_device_info_by_index(default_out)['name'] if default_out >= 0 else ""
        if default_name:
            print(f">>> Default output: [{default_out}] {default_name}", flush=True)

        # FAST PATH (the common case): the DEFAULT OUTPUT device's own loopback IS
        # "system audio" by definition — it's where the interviewer's voice plays.
        # Match it by name and use it immediately, with NO per-device signal probing.
        # That probe added ~2.5s to every cold start for no benefit here, so speech
        # right after opening the app is now captured seconds sooner.
        for i, dev in enumerate(candidates):
            if (default_name and default_name[:25] in dev['name']
                    and 'microphone' not in dev['name'].lower()):
                # One short read to confirm it answers. It is the right device by
                # definition, but "right" and "working" are different claims, and
                # committing to a frozen one costs the whole session's system
                # audio the moment real sound arrives.
                responded, _ = _probe_device(p, dev, timeout_sec=0.4)
                if not responded:
                    print(f">>> Default output loopback [{dev['index']}] did not answer; "
                          f"looking for another.", flush=True)
                    break
                _active_loopback_index = i
                print(f">>> WASAPI loopback selected (default output): "
                      f"[{dev['index']}] {dev['name'][:45]}", flush=True)
                return (dev['index'], dev['rate'], dev['channels'])

        # Fallback (rare — no default-output loopback): probe signals to find the
        # loudest real (non-microphone) output, else the first non-mic candidate.
        SIGNAL_THRESHOLD = 50
        best_pos = 0
        best_level = 0
        first_responsive = None
        for i, dev in enumerate(candidates):
            is_mic = 'microphone' in dev['name'].lower()
            responded, level = _probe_device(p, dev, timeout_sec=0.4)
            print(f">>> Signal [{dev['index']}] {dev['name'][:50]}: "
                  f"amp={level}{'' if responded else '  (NO RESPONSE)'}", flush=True)
            if is_mic or not responded:
                continue
            if first_responsive is None:
                first_responsive = i
            if level > best_level:
                best_level = level
                best_pos   = i

        if best_level > SIGNAL_THRESHOLD:
            _active_loopback_index = best_pos
            dev = candidates[best_pos]
            print(f">>> WASAPI loopback selected (active audio): [{dev['index']}] amp={best_level}", flush=True)
            return (dev['index'], dev['rate'], dev['channels'])

        # Nothing was playing, which is normal before the meeting starts. Take the
        # first device that at least answered. A silent device that responds will
        # carry audio once there is some; one that never responds never will, and
        # picking it was how system audio died on a machine whose real speakers
        # had been excluded.
        if first_responsive is not None:
            _active_loopback_index = first_responsive
            dev = candidates[first_responsive]
            print(f">>> WASAPI loopback selected (idle but responsive): "
                  f"[{dev['index']}] {dev['name'][:45]}", flush=True)
            return (dev['index'], dev['rate'], dev['channels'])

        print(">>> No loopback device answered. System audio unavailable on this machine; "
              "the microphone still works normally.", flush=True)
        return None

    except Exception as e:
        print(f">>> WASAPI loopback probe failed: {e}", flush=True)
        return None


def _try_next_loopback():
    """
    Round-robin to the next loopback candidate when the current one is silent.

    Returns True when a replacement stream was opened, so callers deciding
    whether to give up on system audio entirely can tell the difference between
    "moved on" and "nothing left to move to".
    """
    global sys_stream, _sys_native_rate, _sys_native_channels, _sys_chunk_frames
    global _active_loopback_index, _sys_use_loopback

    if len(_loopback_candidates) <= 1:
        return False

    # Advance to the next candidate, skipping microphone loopbacks — they are capture
    # devices, never system output, so hot-swapping onto one would silently turn
    # "system audio" into a second mic feed.
    n = len(_loopback_candidates)
    for _ in range(n):
        _active_loopback_index = (_active_loopback_index + 1) % n
        if 'microphone' not in _loopback_candidates[_active_loopback_index]['name'].lower():
            break

    dev      = _loopback_candidates[_active_loopback_index]
    lb_chunk = max(CHUNK_FRAMES, int(CHUNK_FRAMES * dev['rate'] / SAMPLE_RATE))

    print(f">>> HOTSWAP [{dev['index']}] {dev['name'][:45]} ({dev['rate']}Hz {dev['channels']}ch)", flush=True)
    try:
        # Deliberately NOT closing the old stream. _read_stream_timeout's worker
        # thread is a daemon thread that keeps running even after we give up
        # waiting on it (a stream.read() that hangs forever never returns, so the
        # thread never exits) — if that zombie thread is still blocked inside a
        # native Pa_ReadStream call on this exact stream when we close it, that's
        # a use-after-close race at the C level: a raw access violation, not a
        # Python exception, so try/except here cannot catch or prevent the crash.
        # A handful of leaked stream objects across a session (hot-swap is rare —
        # only after ~3.5s of continuous silence) is a far safer trade than that.
        sys_stream = p.open(
            format=pyaudio.paInt16,
            channels=dev['channels'],
            rate=dev['rate'],
            input=True,
            input_device_index=dev['index'],
            frames_per_buffer=lb_chunk,
        )
        _sys_native_rate     = dev['rate']
        _sys_native_channels = dev['channels']
        _sys_chunk_frames    = lb_chunk
        _sys_use_loopback    = True
        return True
    except Exception as e:
        print(f">>> HOTSWAP failed: {e}", flush=True)
        return False


# ── REAL-TIME TOKEN MINTING ───────────────────────────────────────────────────
# The long-lived Speechmatics API key (SM_API_KEY) authenticates fine against the
# batch/management REST APIs, but self-service real-time accounts reject it
# outright on the RT websocket ('not_authorised' on every region) — RT requires
# a short-lived JWT minted via the management API first. Confirmed against this
# account's own key: minting succeeds and returns a token scoped (via its "aud"
# claim) to the account's home region.
SM_MANAGEMENT_URL = "https://mp.speechmatics.com/v1/api_keys?type=rt"

def is_realtime_jwt(token: str) -> bool:
    """The backend's /api/v1/stt/key already mints a short-lived RT JWT server-side
    and hands THAT to the client (not the master key) — so args.key is usually
    already ready to use as-is. Only a Settings-overridden key (the user's own
    raw Speechmatics account key, a plain ~32-char string) needs minting here.
    A JWT is unmistakable: three dot-separated segments, header starting 'eyJ'."""
    parts = token.split('.')
    return len(parts) == 3 and token.startswith('eyJ')


def mint_realtime_jwt(raw_key: str, ttl: int = 60) -> str:
    req = urllib.request.Request(
        SM_MANAGEMENT_URL,
        data=json.dumps({"ttl": ttl}).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {raw_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=10) as resp:
        body = json.loads(resp.read().decode("utf-8"))
    key_value = body.get("key_value", "")
    if not key_value:
        raise RuntimeError(f"no key_value in management API response: {body}")
    return key_value


# ── SPEECHMATICS IMPORT ───────────────────────────────────────────────────────
try:
    from speechmatics.models import ConnectionSettings, TranscriptionConfig, AudioSettings
    from speechmatics.client import WebsocketClient
    import speechmatics
    sm_version = getattr(speechmatics, '__version__', 'unknown')
    print(f">>> speechmatics package: OK (version={sm_version})", flush=True)
    try:
        from packaging import version
        if sm_version != 'unknown' and version.parse(sm_version) < version.parse("1.8.0"):
            print(f">>> WARNING: speechmatics version {sm_version} may be outdated.", flush=True)
            print(">>> Run: pip install --upgrade speechmatics", flush=True)
    except ImportError:
        pass
except ImportError as e:
    print(f">>> FATAL: speechmatics package missing - {e}", flush=True)
    print(">>> Fix: pip install speechmatics", flush=True)
    sys.exit(1)


# ── ARGS ──────────────────────────────────────────────────────────────────────
parser = argparse.ArgumentParser()
parser.add_argument("--device",     type=int,   default=None,
                    help="PyAudio input device index for MIC")
parser.add_argument("--sysdevice",  type=int,   default=None,
                    help="PyAudio input device index for system audio (VB-Cable). Auto-detected if not set.")
parser.add_argument("--max-delay",  type=float, default=0.7,
                    help="Speechmatics max_delay seconds, and the single largest delay between "
                         "someone finishing a sentence and reading an answer to it. The words are "
                         "held this long before they are released, so the app waits for them before "
                         "it can ask anything, and the model it then asks replies in about 0.15s. "
                         "0.7 is the LOWEST the Speechmatics RT API accepts: it rejects anything "
                         "below that with a protocol_error and refuses to connect. Was 0.85, which "
                         "spent an extra 150ms per question for accuracy that max_delay_mode "
                         "'flexible' already protects by extending at word boundaries anyway.")
parser.add_argument("--mode",       type=str,   default="both",
                    choices=["both", "system", "mic"],
                    help="'both' = system audio + mic (default). 'system' = system audio only, mic never "
                         "opened. 'mic' = microphone only, no system audio. "
                         "'mic' exists for the case where system audio cannot be captured at all: on "
                         "macOS the app falls back to it when its CoreAudio tap is refused permission, "
                         "or crashes mid-session. Removing it turned a degraded-but-working fallback "
                         "into an engine that refuses to start, which is the worst direction for a "
                         "fallback to fail in.")
parser.add_argument("--sysfifo",    type=str,   default=None,
                    help="Path to a FIFO carrying system audio as 16kHz mono s16le, used instead of "
                         "opening a loopback device. This is how macOS gets system audio at all: a "
                         "helper process is fed silence there, because it does not inherit the app's "
                         "screen-recording grant, so the app runs its own CoreAudio tap in-process "
                         "and writes the PCM here. Windows opens a WASAPI loopback directly and does "
                         "not need this. Ignored when not given.")
parser.add_argument("--language",   type=str,   default="en",
                    help="Speechmatics transcription language code (en, hi, te, ta, es, fr, de, ...). "
                         "The engine is forced to hear ONLY this language; audio in any other language is "
                         "mapped onto the closest words in it, so this must match the interview's language.")
# Accept "-mode both" as well as "--mode both".
#
# The two apps grew apart on this. The Mac passes single-dash long options and
# argparse rejects them outright, so a shared engine would refuse to start
# there and the failure would look like the engine being broken rather than
# called differently. Normalising here costs nothing, breaks nothing that
# already worked, and means neither app has to change to use the same build.
def _accept_single_dash(argv):
    longs = {"device", "sysdevice", "max-delay", "mode", "language", "sysfifo", "key"}
    fixed = []
    for arg in argv:
        name = arg[1:].split("=", 1)[0]
        if arg.startswith("-") and not arg.startswith("--") and name in longs:
            fixed.append("-" + arg)
        else:
            fixed.append(arg)
    return fixed

args = parser.parse_args(_accept_single_dash(sys.argv[1:]))

# Hard floor: the Speechmatics RT API rejects max_delay < 0.7 with a protocol_error
# and refuses the connection entirely. Clamp so no caller can ever break transcription
# by asking for a faster-than-allowed delay.
if args.max_delay < 0.7:
    print(f">>> max_delay {args.max_delay} is below the Speechmatics minimum; clamping to 0.7", flush=True)
    args.max_delay = 0.7

# ── PROVIDER ROUTING ──────────────────────────────────────────────────────────
# Speechmatics has no model for these languages, so they run on Sarvam AI instead.
# 2-letter code (from --language) -> Sarvam's language-code format.
SARVAM_LANG_MAP = {
    "te": "te-IN",   # Telugu   (Speechmatics does NOT support this)
    "kn": "kn-IN",   # Kannada
    "ml": "ml-IN",   # Malayalam
    "gu": "gu-IN",   # Gujarati
    "pa": "pa-IN",   # Punjabi
    "or": "od-IN",   # Odia
    "as": "as-IN",   # Assamese
}
_USE_SARVAM = args.language in SARVAM_LANG_MAP

# Read API key from environment variable (avoids exposing it in process arguments).
# The Speechmatics key is only required for the Speechmatics path — a Sarvam language
# authenticates with SARVAM_API_KEY instead, so don't hard-fail when it's absent.
# Either name. Windows sets SM_API_KEY, macOS sets SPEECHMATICS_API_KEY, and
# renaming it on one side would break the other for no reason beyond which was
# typed first.
_env_key = (os.environ.get("SM_API_KEY", "")
            or os.environ.get("SPEECHMATICS_API_KEY", "")).strip()
if not _env_key and not _USE_SARVAM:
    print(">>> FATAL: neither SM_API_KEY nor SPEECHMATICS_API_KEY is set.", flush=True)
    sys.exit(1)
args.key = _env_key


# ── PATHS ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Where the app is watching, not where this platform happens to put things.
#
# LOCALAPPDATA is Windows-only, so on macOS this fell through to a temp
# directory: latest.txt, pause.flag and reset.flag would all be written to
# /var/folders/... while the app polled Application Support. The engine would
# run perfectly, the app would show an empty transcript forever, and there
# would be no error at either end. A failure invisible from both sides is worse
# than a loud one, so the app is allowed to say where it is listening.
APP_DATA = os.environ.get("APP_DATA_DIR", "").strip() or os.path.join(
    os.environ.get("LOCALAPPDATA", tempfile.gettempdir()),
    "InterviewCopilot"
)
os.makedirs(APP_DATA, exist_ok=True)

LATEST_FILE    = os.path.join(APP_DATA, "latest.txt")
PAUSE_FLAG     = os.path.join(APP_DATA, "pause.flag")
RESET_FLAG     = os.path.join(APP_DATA, "reset.flag")
RECORD_FLAG    = os.path.join(APP_DATA, "record.flag")
RECORDING_ID_FILE = os.path.join(APP_DATA, "recording.id")
RECORDING_SESSION_NUMBER_FILE = os.path.join(APP_DATA, "recording_session_number.txt")
SHUTDOWN_FLAG  = os.path.join(APP_DATA, "shutdown.flag")
RECORDINGS_DIR = APP_DATA

print(f">>> Script folder : {SCRIPT_DIR}", flush=True)
print(f">>> Data folder   : {APP_DATA}", flush=True)
print(">>> API key       : ********...", flush=True)

# Clear any stale shutdown flag left by a previous crash so we don't immediately exit
try:
    if os.path.exists(SHUTDOWN_FLAG):
        os.remove(SHUTDOWN_FLAG)
        print(">>> Stale shutdown.flag removed at startup.", flush=True)
except Exception:
    pass


# ── RUN TESTS ─────────────────────────────────────────────────────────────────
# The standalone mic test is skipped for a fast cold start — it added ~1s to every
# launch and only duplicated what the real mic-stream open (below) already does,
# including the native-rate fallback. Skipping it means speech right after opening
# the app is captured seconds sooner.
if args.mode == "system":
    print(">>> MODE: system-audio-only -- mic never opened.", flush=True)
elif args.mode == "mic":
    print(">>> MODE: microphone-only -- system audio never opened.", flush=True)


# ── RECORDING STATE ───────────────────────────────────────────────────────────
recording_frames = []
is_recording     = False
active_recording_id = ""
record_lock      = threading.Lock()
MAX_RECORDING_FRAMES = int(90 * 60 * 16000 / 4096)  # 90-minute cap — prevents unbounded growth if C# crashes

def get_recording_id():
    try:
        with open(RECORDING_ID_FILE, "r", encoding="utf-8") as f:
            recording_id = f.read().strip()
        if len(recording_id) == 32 and recording_id.isalnum():
            return recording_id
    except Exception:
        pass
    return "unknown"


def mark_recording_saved(recording_id):
    try:
        path = os.path.join(APP_DATA, f"recording_saved_{recording_id}.flag")
        with open(path, "w", encoding="utf-8") as f:
            f.write("1")
    except Exception as ex:
        print(f">>> Recording completion marker error: {ex}", flush=True)


def get_recording_session_number():
    try:
        with open(RECORDING_SESSION_NUMBER_FILE, "r", encoding="utf-8") as handle:
            value = int(handle.read().strip())
            return value if value > 0 else None
    except (OSError, ValueError):
        return None


def save_recording(recording_id):
    global recording_frames
    with record_lock:
        frames_to_save   = recording_frames[:]
        recording_frames = []

    wf = None
    try:
        if frames_to_save:
            session_number = get_recording_session_number()
            filename = (
                os.path.join(RECORDINGS_DIR, f"interview_{session_number}.wav")
                if session_number is not None
                else os.path.join(RECORDINGS_DIR, f"recording_{recording_id}.wav")
            )
            wf = wave.open(filename, 'wb')
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(16000)
            wf.writeframes(b''.join(frames_to_save))
            print(f">>> Recording saved: {filename}", flush=True)
    except Exception as ex:
        print(f">>> Save error: {ex}", flush=True)
    finally:
        if wf is not None:
            try:
                wf.close()
            except Exception:
                pass
        mark_recording_saved(recording_id)


def stop_recording(save_synchronously):
    global is_recording
    with record_lock:
        if not is_recording:
            return False
        is_recording = False
        recording_id = active_recording_id or get_recording_id()

    print(">>> Recording stopped - saving...", flush=True)
    if save_synchronously:
        save_recording(recording_id)
    else:
        threading.Thread(target=save_recording, args=(recording_id,), daemon=True).start()
    return True


# ── PYAUDIO STREAMS ───────────────────────────────────────────────────────────
CHUNK_FRAMES = 1600   # 100 ms at 16 kHz — smaller chunks = lower end-to-end latency
CHUNK_SECONDS = 0.1   # one chunk of wall time; paces the loop when muted
SAMPLE_RATE  = 16000

def write_devices_file(pa):
    """Write all input devices to devices.txt so the C# Settings window can populate its combo box."""
    try:
        lines = []
        for i in range(pa.get_device_count()):
            info = pa.get_device_info_by_index(i)
            if info.get('maxInputChannels', 0) > 0:
                lines.append(f"{i}|{info['name']}")
        if lines:
            devices_path = os.path.join(APP_DATA, "devices.txt")
            with open(devices_path, "w", encoding="utf-8") as f:
                f.write("\n".join(lines))
            print(f">>> devices.txt written ({len(lines)} devices)", flush=True)
    except Exception as ex:
        print(f">>> WARNING: Could not write devices.txt: {ex}", flush=True)


# ── STREAM-FORMAT GLOBALS — initialised here, overwritten by stream setup ────
# These MUST live before the try-block so the try-block's assignments win.
_sys_native_rate     = SAMPLE_RATE
_sys_native_channels = 1
_sys_chunk_frames    = CHUNK_FRAMES
_sys_use_loopback    = False
_loopback_candidates   = []
_active_loopback_index = 0
_hang_swaps = 0   # loopbacks abandoned this session for hanging
_mic_device_name       = ""   # set once the mic device is resolved below
_silent_chunk_count    = 0
SILENCE_HOTSWAP_LIMIT  = 35
LIVE_THRESHOLD         = 400
_last_pause_state      = True   # engine starts muted; log the first observed transition too
_sys_hang_count        = 0      # consecutive system-audio read hangs/errors (both mode)
SYS_HANG_DISABLE_LIMIT = 1      # a stuck loopback must never delay microphone transcription
SYS_READ_TIMEOUT_SECS  = 0.20   # system audio must never hold up the microphone

try:
    p = pyaudio.PyAudio()
    write_devices_file(p)

    # ── MIC STREAM — only opened in "both" mode ──────────────────────────────
    mic_stream = None
    if args.mode in ("both", "mic"):
        mic_kwargs = dict(
            format=pyaudio.paInt16,
            channels=1,
            rate=SAMPLE_RATE,
            input=True,
            frames_per_buffer=CHUNK_FRAMES,
        )
        # Legacy MME passthrough devices ("Microsoft Sound Mapper - Input",
        # "Primary Sound Capture Driver") are not real capture endpoints — they
        # proxy the default device and are prone to closing mid-session, which was
        # crashing the engine into a restart loop. If one is selected, ignore it and
        # fall back to the real default input.
        _LEGACY_MME = ("microsoft sound mapper", "primary sound capture")
        chosen_device = args.device
        if chosen_device is not None:
            try:
                nm = p.get_device_info_by_index(chosen_device).get('name', '').lower()
                if any(k in nm for k in _LEGACY_MME):
                    print(f">>> MIC device [{chosen_device}] is a legacy passthrough "
                          f"({nm}) — using the real default input instead.", flush=True)
                    chosen_device = None
            except Exception:
                chosen_device = None

        if chosen_device is not None:
            mic_kwargs["input_device_index"] = chosen_device
            dev_name = p.get_device_info_by_index(chosen_device)['name']
            _mic_device_name = dev_name
            print(f">>> MIC device [{chosen_device}]: {dev_name}", flush=True)
        else:
            _mic_device_name = p.get_default_input_device_info().get('name', '')
            print(f">>> MIC: using default input device ({_mic_device_name})", flush=True)

        _mic_native_rate     = SAMPLE_RATE
        _mic_native_channels = 1
        _mic_chunk_frames    = CHUNK_FRAMES

        try:
            mic_stream = open_stream_with_timeout(p, mic_kwargs)
            print(">>> MIC stream opened OK (16kHz mono)", flush=True)
        except Exception as ex_mic1:
            print(f">>> MIC 16kHz open failed ({ex_mic1}) — attempting native device rate fallback...", flush=True)
            try:
                target_dev_idx = chosen_device if chosen_device is not None else p.get_default_input_device_info()['index']
                dev_info = p.get_device_info_by_index(target_dev_idx)
                n_rate = int(dev_info.get('defaultSampleRate', 44100))
                n_ch   = max(1, min(int(dev_info.get('maxInputChannels', 1)), 2))
                n_chunk = max(CHUNK_FRAMES, int(CHUNK_FRAMES * n_rate / SAMPLE_RATE))

                mic_kwargs["rate"] = n_rate
                mic_kwargs["channels"] = n_ch
                mic_kwargs["frames_per_buffer"] = n_chunk
                mic_kwargs["input_device_index"] = target_dev_idx

                mic_stream = open_stream_with_timeout(p, mic_kwargs)
                _mic_native_rate     = n_rate
                _mic_native_channels = n_ch
                _mic_chunk_frames    = n_chunk
                print(f">>> MIC stream opened OK with native settings: {n_rate}Hz {n_ch}ch", flush=True)
            except Exception as ex_mic2:
                print(f">>> WARNING: Mic fallback failed ({ex_mic2})", flush=True)
                mic_stream = None
    else:
        print(">>> MIC: skipped (system-audio-only mode)", flush=True)

    # Microphone-only means exactly that. This is the fallback for a machine
    # where system audio cannot be captured at all, so hunting for a loopback
    # device here would spend seconds probing hardware that is already known
    # not to work, and could succeed onto the wrong thing.
    if args.mode == "mic":
        sys_stream = None
        print(">>> SYSTEM AUDIO: skipped (microphone-only mode).", flush=True)

    # ── SYSTEM AUDIO FROM A FIFO (macOS) ─────────────────────────────────────────
    #
    # On macOS a helper process cannot capture system audio at all. It does not
    # inherit the app's screen-recording grant, so the OS hands it silence
    # rather than an error — measured at peak 0.000, which looks exactly like a
    # quiet room and is why this was hard to diagnose.
    #
    # So the Mac app runs its own CoreAudio tap inside the app process, writes
    # 16kHz mono s16le to a FIFO, and passes the path here. Windows opens a
    # WASAPI loopback directly and never sets this.
    #
    # Presented as something with a .read() so the rest of the engine — the
    # mixer, the hot-swap logic, the level probes — needs no knowledge of it.
    if args.sysfifo and args.mode != "mic":
        class _FifoStream:
            """
            A PyAudio-shaped reader over a FIFO of 16kHz mono s16le.

            The first version of this died the moment a writer closed, which on
            macOS is a normal event rather than an edge case: the app's CoreAudio
            tap stops and starts while the engine keeps running, and every mode
            switch restarts it. Measured failure was 85,895 lines of "I/O
            operation on closed file" in fourteen seconds — about 6,100 a second,
            never recovering, and enough to flush every other diagnostic out of a
            rotating log within seconds. The transcript was gone for the rest of
            the session.

            Two faults compounded. The reopen closed the handle and then called a
            blocking open() on a FIFO with no writer, which does not return; and
            because the closed handle was left in place, every later read raised
            on it rather than retrying. The object was permanently broken while
            looking alive.

            So: never block, never leave a dead handle behind, and use what a
            non-blocking FIFO already tells us. Opened O_NONBLOCK, a read raises
            BlockingIOError while a writer is attached but quiet, and returns
            empty only when every writer has gone. Those are exactly the two
            cases that need telling apart, and they need no timers to
            distinguish.
            """

            COMPLAIN_EVERY = 5.0    # seconds between log lines, at most

            def __init__(self, path):
                self.path = path
                self._fd = None
                self._next_complain = 0.0
                self._open()

            def _open(self):
                """Attach if a writer is there. Never blocks, never raises.

                Attempted on every read that finds no handle, not on a timer.
                A throttle here was costing up to half a second of audio each
                time the tap cycled — and the tap cycles on every mode switch,
                so that is the interviewer's speech, not idle time. Opening a
                FIFO with O_NONBLOCK returns immediately whether or not a writer
                is there, so there is nothing to protect against by waiting.
                Only the complaining is throttled.
                """
                try:
                    flags = os.O_RDONLY
                    if hasattr(os, "O_NONBLOCK"):
                        flags |= os.O_NONBLOCK
                    self._fd = os.open(self.path, flags)
                except Exception as e:
                    self._fd = None
                    self._complain(f"FIFO not ready ({e})")

            def _drop(self):
                """Let go of the handle, so nothing is ever read from a dead one."""
                fd, self._fd = self._fd, None
                if fd is not None:
                    try:
                        os.close(fd)
                    except Exception:
                        pass

            def _complain(self, message):
                now = time.time()
                if now < self._next_complain:
                    return
                self._next_complain = now + self.COMPLAIN_EVERY
                print(f">>> SYS FIFO: {message}", flush=True)

            def read(self, frames, exception_on_overflow=False):
                want = frames * 2                      # s16le mono
                silence = b"\x00" * want

                if self._fd is None:
                    self._open()
                    if self._fd is None:
                        return silence

                buf = b""
                while len(buf) < want:
                    try:
                        chunk = os.read(self._fd, want - len(buf))
                    except BlockingIOError:
                        # A writer is attached and has nothing for us this
                        # instant. Silence for the gap, handle kept.
                        return buf + b"\x00" * (want - len(buf))
                    except Exception as e:
                        self._drop()
                        self._complain(f"read failed, will reattach ({e})")
                        return silence

                    if not chunk:
                        # Empty from a non-blocking FIFO means every writer has
                        # gone. Let the handle go and try again shortly; the tap
                        # comes back and so does the transcript.
                        self._drop()
                        self._complain("writer closed, waiting for it to return")
                        return buf + b"\x00" * (want - len(buf))

                    buf += chunk
                return buf

            def stop_stream(self):
                pass

            def close(self):
                self._drop()

        try:
            sys_stream = _FifoStream(args.sysfifo)
            _sys_native_rate     = SAMPLE_RATE
            _sys_native_channels = 1
            _sys_chunk_frames    = CHUNK_FRAMES
            _sys_use_loopback    = False
            print(f">>> SYSTEM AUDIO (FIFO): {args.sysfifo} 16000Hz 1ch", flush=True)
        except Exception as fifo_error:
            print(f">>> FIFO open failed ({fifo_error}) — no system audio this session.", flush=True)
            sys_stream = None

    # ── SYSTEM AUDIO STREAM ──────────────────────────────────────────────────────
    # Priority: 1) WASAPI loopback (captures default output device — no VB-Cable needed)
    #           2) Explicit --sysdevice arg  3) VB-Cable
    if args.mode == "mic":
        sys_device_index = None
    elif args.sysfifo:
        # The FIFO above is the system audio. Hunting for a loopback device as
        # well would open a second source and mix the machine's own output into
        # a feed that already has it.
        sys_device_index = None
    else:
        sys_stream = None
        sys_device_index = args.sysdevice

    # 1. Try WASAPI loopback unless the user pinned an explicit device
    if args.mode != "mic" and not args.sysfifo and sys_device_index is None:
        loopback = find_wasapi_loopback_device(p)
        if loopback is not None:
            lb_idx, lb_rate, lb_ch = loopback
            # Chunk size at native rate that equals CHUNK_FRAMES at 16 kHz
            lb_chunk = max(CHUNK_FRAMES, int(CHUNK_FRAMES * lb_rate / SAMPLE_RATE))
            try:
                sys_stream = open_stream_with_timeout(p, dict(
                    format=pyaudio.paInt16,
                    channels=lb_ch,
                    rate=lb_rate,
                    input=True,
                    input_device_index=lb_idx,
                    frames_per_buffer=lb_chunk,
                ))
                dev_name = p.get_device_info_by_index(lb_idx)['name']
                print(f">>> SYSTEM AUDIO (WASAPI loopback): [{lb_idx}] {dev_name} {lb_rate}Hz {lb_ch}ch", flush=True)
                _sys_native_rate     = lb_rate
                _sys_native_channels = lb_ch
                _sys_chunk_frames    = lb_chunk
                _sys_use_loopback    = True
            except Exception as e:
                print(f">>> WASAPI loopback open failed: {e} — trying VB-Cable.", flush=True)
                sys_stream = None

    # 2. Fall back to VB-Cable (or explicit --sysdevice)
    if sys_stream is None and args.mode != "mic":
        if sys_device_index is None:
            sys_device_index = find_vbcable_device(p)
        if sys_device_index is not None:
            try:
                sys_stream = open_stream_with_timeout(p, dict(
                    format=pyaudio.paInt16,
                    channels=1,
                    rate=SAMPLE_RATE,
                    input=True,
                    input_device_index=sys_device_index,
                    frames_per_buffer=CHUNK_FRAMES,
                ))
                sys_name = p.get_device_info_by_index(sys_device_index)['name']
                print(f">>> SYSTEM AUDIO (VB-Cable): [{sys_device_index}] {sys_name}", flush=True)
                _sys_native_rate     = SAMPLE_RATE
                _sys_native_channels = 1
                _sys_chunk_frames    = CHUNK_FRAMES
                _sys_use_loopback    = False
            except Exception as e:
                print(f">>> WARNING: Could not open system audio stream: {e}", flush=True)
                sys_stream = None
        else:
            print(">>> SYSTEM AUDIO: not available (mic only mode)", flush=True)

except Exception as e:
    print(f">>> FATAL: Cannot open audio stream - {e}", flush=True)
    try: p.terminate()
    except Exception: pass
    sys.exit(1)

SILENCE = b"\x00" * (CHUNK_FRAMES * 2)


def disable_unresponsive_system_audio(reason: str):
    """
    Give up on a stuck loopback stream, after trying the other candidates.

    One device hanging used to end system audio for the whole session, which is
    the wrong conclusion to draw from one bad device. A machine typically offers
    several loopbacks and only one of them is the speakers the meeting is
    actually playing through; the others belong to virtual cables and capture
    tools that accept a stream and then never return a buffer. Picking one of
    those and stopping meant the interviewer was never heard again, and the only
    trace was a line in a debug log the user has no reason to open.

    So the next candidate is tried first, and system audio is only abandoned
    once every one of them has failed.
    """
    global sys_stream, _hang_swaps
    if sys_stream is None:
        return

    # A timeout leaves a daemon thread inside PyAudio's native read. Closing the
    # stream under that thread can crash the interpreter, so abandon the
    # reference and let the thread finish on its own.
    sys_stream = None

    print(f">>> SYSTEM AUDIO: [{_active_loopback_index}] stopped responding — {reason}.", flush=True)

    # Each candidate gets one chance. Without the cap a machine whose loopbacks
    # all hang would rotate between them for the whole session, paying a read
    # timeout every time and never settling.
    _hang_swaps += 1
    if _hang_swaps < len(_loopback_candidates) and _try_next_loopback():
        print(">>> SYSTEM AUDIO: switched to the next output device.", flush=True)
        return

    print(">>> SYSTEM AUDIO unavailable — no output device returned audio. "
          "Running mic-only; your voice still transcribes normally.", flush=True)


def mix_audio(mic_data: bytes, sys_data: bytes) -> bytes:
    """Additively mix mic + system audio, clamped to the int16 range.
    Additive (not averaged): when system audio is silent — the normal case in
    mic+system mode with nothing playing — the mic stays at full volume. The old
    ``(m + s) // 2`` halved quiet speech, often below the transcription threshold.
    Length-safe: mixes only the overlapping span so a short/long buffer can't raise
    struct.error and silently collapse the stream to mic-only."""
    count = min(len(mic_data), len(sys_data)) // 2
    if count == 0:
        return mic_data if mic_data else sys_data

    mic_level = _signal_level(mic_data)
    sys_level = _signal_level(sys_data)
    if sys_level <= 125:
        return mic_data
    if mic_level <= 125:
        return sys_data
    if mic_level >= sys_level * 1.8:
        return mic_data
    if sys_level >= mic_level * 1.8:
        return sys_data

    fmt = f'<{count}h'
    mic_samples = struct.unpack_from(fmt, mic_data)
    sys_samples = struct.unpack_from(fmt, sys_data)
    mixed = [max(-32768, min(32767, int((m * 0.65) + (s * 0.65))))
             for m, s in zip(mic_samples, sys_samples)]
    return struct.pack(fmt, *mixed)


def resample_to_16k_mono(data: bytes, src_rate: int, src_channels: int, out_frames: int) -> bytes:
    """
    Convert PCM S16LE at src_rate/src_channels → 16000 Hz mono.
    Uses linear interpolation — no external libraries needed.
    out_frames: exact number of output samples required.
    """
    n = len(data) // 2
    samples = struct.unpack_from(f'<{n}h', data)

    # Stereo (or multi-channel) → mono
    if src_channels > 1:
        mono_count = n // src_channels
        mono = []
        for i in range(mono_count):
            chunk = samples[i * src_channels:(i + 1) * src_channels]
            mono.append(max(-32768, min(32767, sum(chunk) // src_channels)))
        samples = mono

    src_len = len(samples)
    if src_rate == 16000:
        out = list(samples[:out_frames])
    elif src_rate > 16000 and out_frames > 0:
        # DOWNSAMPLING (e.g. 48kHz system-audio loopback -> 16kHz). Average every
        # source sample that maps to an output sample instead of point/linear picking.
        # Plain interpolation has NO anti-aliasing, so frequencies above 8kHz fold back
        # into the voice band and garble the interviewer's audio — a real accuracy hit.
        # Averaging the window acts as a low-pass filter and removes most of that aliasing.
        ratio = src_len / out_frames
        out = []
        for i in range(out_frames):
            start = int(i * ratio)
            end   = int((i + 1) * ratio)
            if end <= start:
                end = start + 1
            if end > src_len:
                end = src_len
            if start >= src_len:
                out.append(0)
                continue
            acc = 0
            for j in range(start, end):
                acc += samples[j]
            out.append(max(-32768, min(32767, acc // (end - start))))
    else:
        # Upsampling or equal-ish rate: linear interpolation is fine (no aliasing risk).
        ratio = src_len / out_frames if out_frames else 1.0
        out = []
        for i in range(out_frames):
            pos  = i * ratio
            i0   = int(pos)
            i1   = min(i0 + 1, src_len - 1)
            frac = pos - i0
            val  = int(samples[i0] * (1.0 - frac) + samples[i1] * frac)
            out.append(max(-32768, min(32767, val)))

    # Pad/trim to exactly out_frames
    if len(out) < out_frames:
        out += [0] * (out_frames - len(out))
    else:
        out = out[:out_frames]

    return struct.pack(f'<{out_frames}h', *out)


# (stream-format globals are initialised before the try-block above)


# ── TRANSCRIPT TEXT BUILDER ───────────────────────────────────────────────────
# Adaptive confidence filtering keeps quiet speech while removing isolated guesses.
# Quiet voices often produce uniformly lower confidence, so they use a gentler word
# floor. Clear segments use the stronger floor to prevent random background words.
CONFIDENCE_THRESHOLD       = 0.35
QUIET_CONFIDENCE_THRESHOLD = 0.18
QUIET_SEGMENT_CEILING      = 0.45
SEGMENT_CONF_FLOOR         = 0.16

def build_text_from_results(results):
    # Compute average confidence across word tokens (punctuation excluded from avg)
    word_confs = [
        res["alternatives"][0].get("confidence", 1.0)
        for res in results
        if res.get("alternatives") and res.get("type") != "punctuation"
    ]
    average_confidence = sum(word_confs) / len(word_confs) if word_confs else 1.0
    if word_confs and average_confidence < SEGMENT_CONF_FLOOR:
        avg = average_confidence
        words = [r["alternatives"][0].get("content", "") for r in results if r.get("alternatives")]
        print(f">>> DEBUG: segment DROPPED (avg confidence {avg:.2f} < {SEGMENT_CONF_FLOOR}) — words were: {words}", flush=True)
        return ""   # skip entire low-confidence segment

    word_confidence_floor = (
        QUIET_CONFIDENCE_THRESHOLD
        if average_confidence < QUIET_SEGMENT_CEILING
        else CONFIDENCE_THRESHOLD
    )

    if not results:
        print(">>> DEBUG: empty results array from Speechmatics (no words recognized yet)", flush=True)

    text = ""
    dropped_words = []
    for res in results:
        if not res.get("alternatives"):
            continue
        alt     = res["alternatives"][0]
        word    = alt["content"]
        is_punc = res.get("type") == "punctuation"
        if not is_punc and alt.get("confidence", 1.0) < word_confidence_floor:
            dropped_words.append(f"{word}({alt.get('confidence', 1.0):.2f})")
            continue   # drop low-confidence word
        if is_punc:
            text = text.rstrip() + word + " "
        else:
            text += word + " "

    if results and not text.strip() and dropped_words:
        print(f">>> DEBUG: every word this segment fell below confidence floor "
              f"({word_confidence_floor:.2f}) — dropped: {dropped_words}", flush=True)

    return text


# ── INDEPENDENT WATCHDOG THREAD ────────────────────────────────────────────────
# Runs on its own wall-clock timer, completely decoupled from MixedStream.read()'s
# call cadence (which depends on the SDK/PyAudio and may not be a steady 100ms —
# a slow-draining WASAPI loopback device, for example, could throttle it far more
# than expected). This gives ground truth on pause.flag regardless of that.
def _pause_flag_watchdog():
    last = None
    while True:
        try:
            cur = os.path.exists(PAUSE_FLAG)
            if cur != last:
                print(f">>> WATCHDOG: pause.flag {'SET' if cur else 'CLEARED'} "
                      f"(path={PAUSE_FLAG})", flush=True)
                last = cur
        except Exception as ex:
            print(f">>> WATCHDOG error: {ex}", flush=True)
        time.sleep(0.25)

threading.Thread(target=_pause_flag_watchdog, daemon=True).start()


# ══════════════════════════════════════════════════════════════════════════════
# SARVAM AI ENGINE  (for Telugu + other languages Speechmatics can't do)
# Fully independent of the Speechmatics path below — reuses only the already-open
# audio streams, the resampler, and the pause/reset/shutdown flags.
# ══════════════════════════════════════════════════════════════════════════════
def _load_extra_vocab():
    """Interview-specific terms (company name, the role's tech stack, names/projects from
    the resume) that C# writes to vocab.txt. Feeding these to Speechmatics makes it far
    more accurate on exactly the words that matter in THIS interview instead of guessing."""
    terms = []
    try:
        path = os.path.join(APP_DATA, "vocab.txt")
        if os.path.exists(path):
            seen = set()
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    w = line.strip()
                    key = w.lower()
                    if w and 1 < len(w) <= 40 and key not in seen:
                        seen.add(key)
                        terms.append({"content": w})
                    if len(terms) >= 180:   # Speechmatics caps additional_vocab size
                        break
    except Exception as e:
        print(f">>> vocab.txt load skipped: {e}", flush=True)
    if terms:
        print(f">>> Loaded {len(terms)} interview-specific vocab terms for accuracy", flush=True)
    return terms


def _write_latest(text):
    """Atomically publish the current transcript to latest.txt (same contract as the
    Speechmatics path's nested _write)."""
    try:
        tmp = LATEST_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(text)
        try:
            os.replace(tmp, LATEST_FILE)
        except OSError:
            with open(LATEST_FILE, "w", encoding="utf-8") as f:
                f.write(text)
            try:
                os.remove(tmp)
            except Exception:
                pass
    except Exception as fe:
        print(f">>> File write error: {fe}", flush=True)


def _mix_pcm16(a: bytes, b: bytes) -> bytes:
    """Average two equal-length s16le mono buffers (mic + system audio)."""
    n = min(len(a), len(b)) // 2
    if n == 0:
        return a or b
    sa = struct.unpack_from(f"<{n}h", a)
    sb = struct.unpack_from(f"<{n}h", b)
    out = [max(-32768, min(32767, (sa[i] + sb[i]) // 2)) for i in range(n)]
    return struct.pack(f"<{n}h", *out)


_sarvam_sys_hang = 0
_sarvam_sys_dead = False

def _sarvam_read_chunk() -> bytes:
    """Read one ~100ms chunk of 16kHz mono PCM from whatever streams are open,
    mixing mic + system audio and resampling as needed. Hang-safe (timeouts).
    An idle system-audio loopback (nothing playing) hangs on read; after enough
    consecutive hangs we drop it for the session so it can't throttle the mic —
    same strategy the Speechmatics path uses."""
    global _sarvam_sys_hang, _sarvam_sys_dead
    mic_data = b""
    sys_data = b""
    if mic_stream is not None:
        raw = _read_stream_timeout(mic_stream, _mic_chunk_frames, 0.5, "SARVAM-MIC")
        if raw:
            mic_data = (resample_to_16k_mono(raw, _mic_native_rate, _mic_native_channels, CHUNK_FRAMES)
                        if (_mic_native_rate != SAMPLE_RATE or _mic_native_channels != 1) else raw)
    if sys_stream is not None and not _sarvam_sys_dead:
        raw = _read_stream_timeout(sys_stream, _sys_chunk_frames, 0.2, "SARVAM-SYS")
        if raw:
            _sarvam_sys_hang = 0
            sys_data = (resample_to_16k_mono(raw, _sys_native_rate, _sys_native_channels, CHUNK_FRAMES)
                        if (_sys_native_rate != SAMPLE_RATE or _sys_native_channels != 1) else raw)
        else:
            _sarvam_sys_hang += 1
            if _sarvam_sys_hang >= 12:
                _sarvam_sys_dead = True
                print(">>> [SARVAM] system audio idle/hanging — mic only for this session "
                      "(restart to retry system audio).", flush=True)
    if mic_data and sys_data:
        return _mix_pcm16(mic_data, sys_data)
    return mic_data or sys_data


async def run_sarvam():
    """Real-time transcription via Sarvam AI's streaming WebSocket. Mirrors the
    Speechmatics path's behaviour: streams live audio, honours the mute (pause.flag)
    and reset (reset.flag) toggles, writes to latest.txt, and auto-reconnects."""
    import websockets  # transitive dep of the speechmatics SDK; imported lazily

    api_key = os.environ.get("SARVAM_API_KEY", "").strip()
    if not api_key:
        print(">>> FATAL: SARVAM_API_KEY not set — this language needs a Sarvam key. "
              "Add it in Settings.", flush=True)
        sys.exit(2)

    lang_code = SARVAM_LANG_MAP.get(args.language, "te-IN")
    qs = (f"language-code={lang_code}&model=saarika:v2.5&mode=transcribe"
          f"&sample_rate={SAMPLE_RATE}&input_audio_codec=pcm_s16le")
    url = f"wss://api.sarvam.ai/speech-to-text/ws?{qs}"
    headers = {"Api-Subscription-Key": api_key}

    print("", flush=True)
    print("===============================================", flush=True)
    print(f"   SARVAM ENGINE: READY  (language={lang_code})", flush=True)
    print("===============================================", flush=True)

    reconnect_delay = 3
    loop = asyncio.get_event_loop()

    while True:
        if os.path.exists(SHUTDOWN_FLAG):
            print(">>> Shutdown flag detected. Exiting cleanly.", flush=True)
            try:
                os.remove(SHUTDOWN_FLAG)
            except Exception:
                pass
            return
        try:
            print(f">>> [SARVAM] Connecting ({lang_code})...", flush=True)
            async with websockets.connect(url, additional_headers=headers,
                                          max_size=None, ping_interval=20) as ws:
                print(">>> STATUS: ONLINE ✓", flush=True)
                reconnect_delay = 3
                state = {"text": ""}
                stop  = asyncio.Event()

                async def sender():
                    global _last_pause_state
                    while not stop.is_set():
                        if os.path.exists(SHUTDOWN_FLAG):
                            stop.set()
                            break
                        paused = os.path.exists(PAUSE_FLAG)
                        if paused != _last_pause_state:
                            print(f">>> {'PAUSED (pause.flag set)' if paused else 'RESUMED (pause.flag cleared) - now reading real audio'}", flush=True)
                            _last_pause_state = paused
                        chunk = await loop.run_in_executor(None, _sarvam_read_chunk)
                        if paused or not chunk:
                            await asyncio.sleep(0.005)
                            continue
                        try:
                            b64 = base64.b64encode(chunk).decode("utf-8")
                            await ws.send(json.dumps({"audio": {
                                "data": b64, "sample_rate": str(SAMPLE_RATE),
                                "encoding": "audio/wav"}}))
                        except Exception:
                            stop.set()
                            break

                async def receiver():
                    async for raw in ws:
                        if stop.is_set():
                            break
                        if os.path.exists(RESET_FLAG):
                            state["text"] = ""
                            try:
                                os.remove(RESET_FLAG)
                            except Exception:
                                pass
                        if os.path.exists(PAUSE_FLAG):
                            continue
                        try:
                            m = json.loads(raw)
                        except Exception:
                            continue
                        if m.get("type") == "data":
                            seg = ((m.get("data") or {}).get("transcript") or "").strip()
                            if seg:
                                state["text"] = (state["text"] + " " + seg).strip()
                                print(f">>> [SARVAM] segment ({len(state['text'])} chars)", flush=True)
                                _write_latest(state["text"])

                try:
                    await asyncio.gather(sender(), receiver())
                finally:
                    stop.set()
        except Exception as e:
            msg = str(e)
            # A bad / expired Sarvam key rejects the handshake with HTTP 401/403. Don't
            # retry that forever in silence — exit with the auth code so the app can show
            # "Fix your Sarvam key in Settings" instead of looking frozen.
            if "401" in msg or "403" in msg or "Unauthorized" in msg or "Forbidden" in msg:
                print(">>> FATAL: Sarvam auth failed (exit 2) — check your Sarvam API key in Settings.", flush=True)
                sys.exit(2)
            print(">>> STATUS: OFFLINE", flush=True)
            print(f">>> [SARVAM] disconnected/error: {e}", flush=True)
            await asyncio.sleep(reconnect_delay)
            reconnect_delay = min(reconnect_delay * 2, 30)


# Which recognition model to ask for.
#
# "enhanced", not melia-1, and not by oversight. Melia-1 is Speechmatics'
# newest and most accurate model on paper, and it validates against a
# different schema that rejects almost everything this engine depends on:
#
#   additional_vocab        the interview's own vocabulary
#   punctuation_overrides   the sentence endings Auto uses to detect a turn
#   enable_entities         numbers and names formatted properly
#   max_delay               the latency control, tuned to its 0.7s floor
#   max_delay_mode          the flexible extension at word boundaries
#
# Requesting it was tried and took transcription down completely: every
# endpoint refused the session, on every retry, and the app could not hear
# anything at all. Better accuracy on a model that cannot be told which words
# to expect, cannot be told when a sentence ended, and cannot be tuned for
# latency is not better accuracy for this product.
#
# Revisit only alongside rebuilding turn detection and vocabulary around
# whatever melia-1 does support.
_speech_model = "enhanced"


def _downgrade_model_if_rejected(err_text: str) -> bool:
    """True when the error was the model being refused, and a downgrade happened."""
    global _speech_model
    if _speech_model != "melia-1":
        return False
    # The refusal does not mention the model by name. Melia-1 validates against
    # a different schema, so what comes back is a list of properties that are
    # suddenly not allowed: additional_vocab, punctuation_overrides,
    # enable_entities, max_delay, max_delay_mode. The first version of this
    # matched on "not_allowed" with an underscore and the API says "is not
    # allowed" with a space, so nothing matched, nothing downgraded, and every
    # endpoint refused every retry with no transcription at all.
    lowered = str(err_text).lower()
    if any(k in lowered for k in
           ("is not allowed", "oneof", "invalid input", "protocol_error",
            "operating_point", "melia", "invalid_model", "unsupported",
            "forbidden", "403")):
        _speech_model = "enhanced"
        print(">>> melia-1 unavailable on this account; using enhanced for this session.",
              flush=True)
        return True
    return False


# ── MAIN WITH AUTO-RECONNECT ──────────────────────────────────────────────────
async def main():
    # Route Speechmatics-unsupported languages (Telugu, etc.) to Sarvam AI and skip
    # the entire Speechmatics path below.
    if _USE_SARVAM:
        await run_sarvam()
        return

    print("", flush=True)
    print("===============================================", flush=True)
    print("   SPEECHMATICS ENGINE: READY", flush=True)
    if args.mode == "system":
        if sys_stream:
            print("   MODE: SYSTEM AUDIO ONLY (mic never opened)", flush=True)
        else:
            print("   MODE: SYSTEM AUDIO ONLY — but VB-Cable not found!", flush=True)
    elif sys_stream:
        print("   MODE: MIC + SYSTEM AUDIO (mixed)", flush=True)
    else:
        print("   MODE: MIC ONLY (install VB-Cable for system audio)", flush=True)
    print(f"   max_delay={args.max_delay}s  max_delay_mode=flexible  chunk={CHUNK_FRAMES}frames", flush=True)
    print("===============================================", flush=True)

    # EU listed first: Speechmatics self-service keys are region-locked to the
    # account's signup region (visible in the minted JWT's "aud" claim), and
    # rejecting on the wrong region's endpoint looks identical to a genuinely
    # bad key ("not_authorised"). Trying the account's real region first avoids
    # masking a working key behind a same-error wrong-region attempt.
    endpoints = [
        "wss://eu.rt.speechmatics.com/v2",
        "wss://us.rt.speechmatics.com/v2",
    ]

    reconnect_delay = 3
    attempt         = 0

    while True:
        # Graceful shutdown — C# writes this flag before killing the process
        if os.path.exists(SHUTDOWN_FLAG):
            print(">>> Shutdown flag detected. Exiting cleanly.", flush=True)
            try:
                os.remove(SHUTDOWN_FLAG)
            except Exception:
                pass
            return

        attempt  += 1
        connected = False
        auth_errors = 0

        for endpoint in endpoints:
            try:
                raw_key = args.key.strip()
                if is_realtime_jwt(raw_key):
                    rt_token = raw_key   # already a short-lived RT token minted by our backend
                else:
                    try:
                        rt_token = mint_realtime_jwt(raw_key)   # raw account key (Settings override) — mint first
                    except Exception as mint_ex:
                        print(f">>> Failed to mint real-time token for {endpoint}: {mint_ex}", flush=True)
                        auth_errors += 1
                        if auth_errors >= len(endpoints):
                            print(">>> FATAL: Could not obtain a real-time auth token. Check your Speechmatics key in Settings.", flush=True)
                            sys.exit(2)
                        continue

                print(f">>>[Attempt {attempt}] Connecting to {endpoint}...", flush=True)

                settings = ConnectionSettings(
                    url        = endpoint,
                    auth_token = rt_token,
                )

                ws = WebsocketClient(settings)

                confirmed_text = ""
                partial_text   = ""

                def handle_final(msg):
                    nonlocal confirmed_text, partial_text
                    try:
                        if os.path.exists(PAUSE_FLAG):
                            return
                        if os.path.exists(RESET_FLAG):
                            confirmed_text = ""
                            partial_text   = ""
                            try:
                                os.remove(RESET_FLAG)
                            except:
                                pass
                            return

                        segment = build_text_from_results(msg.get("results", []))
                        if not segment.strip():
                            return

                        confirmed_text += segment
                        partial_text    = ""

                        display = confirmed_text.strip()
                        print(f">>> FINAL received ({len(display)} chars)", flush=True)
                        _write(display)
                    except Exception as e:
                        print(f">>> handle_final error: {e}", flush=True)

                def handle_partial(msg):
                    nonlocal confirmed_text, partial_text
                    try:
                        if os.path.exists(PAUSE_FLAG):
                            return
                        if os.path.exists(RESET_FLAG):
                            # MUST clear confirmed_text here too. Partials arrive before
                            # finals, so if this handler removed the reset flag without
                            # clearing confirmed_text, the final handler would never see
                            # the flag and the previous question's text would keep
                            # accumulating onto every following question.
                            confirmed_text = ""
                            partial_text   = ""
                            try: os.remove(RESET_FLAG)
                            except: pass
                            return

                        segment      = build_text_from_results(msg.get("results", []))
                        partial_text = segment

                        display = (confirmed_text + partial_text).strip()
                        print(f">>> PARTIAL received ({len(display)} chars)", flush=True)
                        _write(display)
                    except Exception as e:
                        print(f">>> handle_partial error: {e}", flush=True)

                def _write(text):
                    try:
                        tmp = LATEST_FILE + ".tmp"
                        with open(tmp, "w", encoding="utf-8") as f:
                            f.write(text)
                        try:
                            os.replace(tmp, LATEST_FILE)
                        except OSError:
                            # C# may hold latest.txt without delete-share (old binary).
                            # Fall back to direct overwrite — C# opens with ReadWrite so this works.
                            with open(LATEST_FILE, "w", encoding="utf-8") as f:
                                f.write(text)
                            try:
                                os.remove(tmp)
                            except Exception:
                                pass
                    except Exception as fe:
                        print(f">>> File write error: {fe}", flush=True)

                ws.add_event_handler("AddTranscript",        handle_final)
                ws.add_event_handler("AddPartialTranscript", handle_partial)
                ws.add_event_handler("RecognitionStarted",
                                     lambda m: print(">>> STATUS: ONLINE ✓", flush=True))
                ws.add_event_handler("Error",
                                     lambda e: print(f">>> WS ERROR: {e}", flush=True))

                # output_locale only applies to English (en-US / en-GB / ...). For any
                # other language Speechmatics rejects an English locale, so set it only
                # for en and let the chosen language pass straight through otherwise.
                _lang_extra = {"output_locale": "en-US"} if args.language == "en" else {}
                conf = TranscriptionConfig(
                    language        = args.language,
                    **_lang_extra,
                    operating_point = _speech_model,
                    max_delay       = args.max_delay,
                    max_delay_mode  = "flexible",
                    enable_partials = True,
                    punctuation_overrides = {
                        "permitted_marks": [".", ",", "?", "!"],
                        "sensitivity"    : 0.4,
                    },
                    enable_entities  = True,
                    disfluencies     = False,
                    # Words the recogniser is told to expect. Every entry here
                    # biases it toward hearing that word, which is why the list
                    # has to match the interview being had.
                    #
                    # It did not. It carried a previous user's analytical
                    # chemistry vocabulary, HPLC, FTIR, GMP, ALCOA, LIMS, OOS,
                    # OOT, CAPA, IQ/OQ/PQ, Waters Empower, Agilent ChemStation,
                    # alongside their employer and town. Those are not harmless
                    # extras: OOS, OOT and GC are short enough to be substituted
                    # for ordinary words, so a software candidate saying "of
                    # course" or "you use" was being nudged toward pharmaceutical
                    # acronyms they have never said in their life.
                    #
                    # Anyone who genuinely needs those terms gets them from their
                    # own resume through _load_extra_vocab, which is the right
                    # mechanism: specific to that interview, not baked in for
                    # everyone.
                    additional_vocab = [
                        {"content": "Replysis"},
                        {"content": "Speechmatics"},

                        # Languages and runtimes
                        {"content": "TypeScript"},
                        {"content": "JavaScript"},
                        {"content": "Python"},
                        {"content": "Java"},
                        {"content": "Golang",         "sounds_like": ["go lang"]},
                        {"content": "Rust"},
                        {"content": "Kotlin"},
                        {"content": "Swift"},
                        {"content": "C#",             "sounds_like": ["C sharp"]},
                        {"content": "C++",            "sounds_like": ["C plus plus"]},
                        {"content": ".NET",           "sounds_like": ["dot net"]},
                        {"content": "Node.js",        "sounds_like": ["node J S"]},

                        # Frameworks
                        {"content": "React"},
                        {"content": "Angular"},
                        {"content": "Vue"},
                        {"content": "Next.js",        "sounds_like": ["next J S"]},
                        {"content": "Spring Boot"},
                        {"content": "Hibernate"},
                        {"content": "Django"},
                        {"content": "FastAPI",        "sounds_like": ["fast A P I"]},

                        # Data
                        {"content": "SQL",            "sounds_like": ["sequel"]},
                        {"content": "NoSQL",          "sounds_like": ["no sequel"]},
                        {"content": "PostgreSQL",     "sounds_like": ["post gress SQL", "postgres"]},
                        {"content": "MongoDB",        "sounds_like": ["mongo D B"]},
                        {"content": "Redis"},
                        {"content": "Kafka"},
                        {"content": "Elasticsearch"},
                        {"content": "DynamoDB",       "sounds_like": ["dynamo D B"]},

                        # Platform and delivery
                        {"content": "AWS",            "sounds_like": ["A W S"]},
                        {"content": "Azure"},
                        {"content": "Kubernetes",     "sounds_like": ["koo ber net ees"]},
                        {"content": "Docker"},
                        {"content": "Terraform"},
                        {"content": "Jenkins"},
                        {"content": "GitHub"},
                        {"content": "CI/CD",          "sounds_like": ["C I C D"]},
                        {"content": "microservices"},
                        {"content": "serverless"},

                        # Interfaces and auth
                        {"content": "API",            "sounds_like": ["A P I"]},
                        {"content": "REST"},
                        {"content": "GraphQL",        "sounds_like": ["graph Q L"]},
                        {"content": "gRPC",           "sounds_like": ["gee R P C"]},
                        {"content": "OAuth",          "sounds_like": ["oh auth"]},
                        {"content": "JWT",            "sounds_like": ["J W T"]},
                        {"content": "webhook"},

                        # Words interviewers actually use, and that get misheard
                        {"content": "idempotent",     "sounds_like": ["eye dem po tent"]},
                        {"content": "middleware"},
                        {"content": "refactor"},
                        {"content": "scalability"},
                        {"content": "latency"},
                        {"content": "throughput"},
                        {"content": "concurrency"},
                        {"content": "asynchronous"},
                        {"content": "deprecated"},
                        {"content": "schema"},
                        {"content": "regression"},
                        {"content": "onboarding"},
                        {"content": "stakeholder"},
                        {"content": "roadmap"},

                        # How the job itself is worded.
                        #
                        # These are asked in the first two minutes of almost every
                        # US contract screen, and they were the words the engine
                        # got worst, because they are letters and numbers rather
                        # than words: "C2C" came through as "See to see" and "W2"
                        # as "w to". The candidate then heard an answer written
                        # for a question nobody asked.
                        #
                        # Every spelling a person actually says is listed, since
                        # the same term is spoken "C two C", "C to C" and "corp
                        # to corp" by three different recruiters in one week.
                        {"content": "C2C",            "sounds_like": ["see to see", "C to C", "see two see", "C two C"]},
                        {"content": "W2",             "sounds_like": ["W to", "double you two", "W two", "dubya two"]},
                        {"content": "1099",           "sounds_like": ["ten ninety nine", "one thousand ninety nine"]},
                        {"content": "corp to corp",   "sounds_like": ["corp two corp", "core to core"]},
                        {"content": "full time",      "sounds_like": ["full time"]},
                        {"content": "part time"},
                        {"content": "contract to hire", "sounds_like": ["contract two hire", "C two H"]},
                        {"content": "H1B",            "sounds_like": ["H one B", "age one bee", "H 1 B"]},
                        {"content": "OPT",            "sounds_like": ["O P T"]},
                        {"content": "CPT",            "sounds_like": ["C P T"]},
                        {"content": "EAD",            "sounds_like": ["E A D"]},
                        {"content": "green card"},
                        {"content": "visa"},
                        {"content": "notice period"},
                        {"content": "relocation"},
                        {"content": "onsite",         "sounds_like": ["on site"]},
                        {"content": "hybrid"},
                        {"content": "remote"},
                    ] + _load_extra_vocab()
                )

                audio_conf = AudioSettings(
                    encoding    = "pcm_s16le",
                    sample_rate = SAMPLE_RATE,
                    chunk_size  = CHUNK_FRAMES,
                )

                class MixedStream:
                    _call_count = 0
                    _last_heartbeat = 0.0

                    def read(self, num_frames, exception_on_overflow=False):
                        global is_recording, recording_frames, active_recording_id
                        global _last_pause_state
                        global _silent_chunk_count
                        global _sys_hang_count, sys_stream

                        _read_started = time.time()
                        shutdown_requested = os.path.exists(SHUTDOWN_FLAG)
                        recording_requested = os.path.exists(RECORD_FLAG)
                        paused = os.path.exists(PAUSE_FLAG)

                        # One-shot transition log, independent of mic amplitude — proves
                        # whether this process actually observes the C# mute/unmute toggle.
                        if paused != _last_pause_state:
                            print(f">>> {'PAUSED (pause.flag set)' if paused else 'RESUMED (pause.flag cleared) - now reading real audio'}", flush=True)
                            _last_pause_state = paused

                        # Heartbeat, proving read() is still being called and showing the
                        # live paused state. This counted calls rather than time, on the
                        # assumption that a call takes ~100ms. While paused nothing blocks,
                        # so the loop ran far faster and this printed thousands of lines a
                        # second: one measured session reached 128 million calls and a 1.6 GB
                        # log. Timed instead, so the rate no longer depends on how fast the
                        # loop happens to turn.
                        MixedStream._call_count += 1
                        _hb_now = time.time()
                        if _hb_now - MixedStream._last_heartbeat >= 5.0:
                            MixedStream._last_heartbeat = _hb_now
                            print(f">>> HEARTBEAT #{MixedStream._call_count}: paused={paused}", flush=True)

                        if paused:
                            try:
                                if mic_stream:
                                    _read_stream_timeout(mic_stream, num_frames, 0.5, "MIC-drain")
                                if sys_stream:
                                    sys_drain = _read_stream_timeout(
                                        sys_stream,
                                        _sys_chunk_frames,
                                        SYS_READ_TIMEOUT_SECS if args.mode == "both" else 0.5,
                                        "SYS-drain",
                                    )
                                    if sys_drain is None and args.mode == "both":
                                        disable_unresponsive_system_audio(
                                            "loopback did not return audio while idle"
                                        )
                            except:
                                pass
                            if not recording_requested:
                                stopped = stop_recording(shutdown_requested)
                                if not stopped:
                                    mark_recording_saved(get_recording_id())
                            if shutdown_requested and not recording_requested:
                                raise RuntimeError("Shutdown requested")

                            # Pace the idle loop. While paused nothing here necessarily
                            # blocks: if a drain read is skipped or returns at once, this
                            # returns immediately and the consumer calls straight back,
                            # so the loop spun as fast as the CPU allowed. Measured at
                            # roughly 4,800 iterations a second against the 10 it is meant
                            # to run at, holding a full core while the app sat muted, and
                            # writing a 1.6 GB log. Sleeping a chunk's worth costs nothing
                            # while muted, because no audio is being transcribed anyway.
                            _idle_elapsed = time.time() - _read_started
                            if _idle_elapsed < CHUNK_SECONDS:
                                time.sleep(CHUNK_SECONDS - _idle_elapsed)
                            return SILENCE

                        if args.mode == "system":
                            # System-audio-only: never touch the mic stream
                            if sys_stream:
                                try:
                                    raw = _read_stream_timeout(
                                        sys_stream,
                                        _sys_chunk_frames,
                                        SYS_READ_TIMEOUT_SECS,
                                        "SYS",
                                    )
                                    if raw is None:
                                        raw = SILENCE
                                    data = (resample_to_16k_mono(raw, _sys_native_rate, _sys_native_channels, num_frames)
                                            if (_sys_native_rate != SAMPLE_RATE or _sys_native_channels != 1)
                                            else raw)
                                except Exception as re:
                                    print(f">>> sys_stream.read error: {re}", flush=True)
                                    data = SILENCE

                                # Hot-swap: if this device has been silent too long, try next
                                amp = _signal_level(data)
                                if amp >= LIVE_THRESHOLD:
                                    if _silent_chunk_count > 0:
                                        print(f">>> SYS AUDIO live on [{_loopback_candidates[_active_loopback_index]['index'] if _loopback_candidates else '?'}]: amp={amp}", flush=True)
                                    _silent_chunk_count = 0
                                else:
                                    _silent_chunk_count += 1
                                    if _silent_chunk_count % 20 == 0:
                                        dev_name = _loopback_candidates[_active_loopback_index]['name'][:35] if _loopback_candidates else '?'
                                        print(f">>> SYS AUDIO silent {_silent_chunk_count} chunks on [{dev_name}], amp={amp}", flush=True)
                                    if _silent_chunk_count >= SILENCE_HOTSWAP_LIMIT:
                                        _silent_chunk_count = 0
                                        _try_next_loopback()
                                        data = SILENCE
                            else:
                                data = SILENCE
                        else:
                            # Both: read mic, mix with system audio if available
                            try:
                                if mic_stream:
                                    mic_raw = _read_stream_timeout(mic_stream, _mic_chunk_frames, 0.5, "MIC")
                                    if mic_raw is None:
                                        mic_data = SILENCE
                                        mic_amp = 0
                                    else:
                                        mic_data = (resample_to_16k_mono(mic_raw, _mic_native_rate, _mic_native_channels, num_frames)
                                                    if (_mic_native_rate != SAMPLE_RATE or _mic_native_channels != 1)
                                                    else mic_raw)
                                        mic_amp = _signal_level(mic_data)
                                    if mic_amp > 400:
                                        print(f">>> MIC SIGNAL DETECTED: amp={mic_amp}", flush=True)
                                    elif MixedStream._call_count % 3 == 0:
                                        # Below the 400 threshold — still show it so we can tell
                                        # "quiet/no signal at all" (amp near 0) apart from
                                        # "signal present but too quiet to count" (amp in the
                                        # tens/hundreds).
                                        print(f">>> mic ambient amp={mic_amp} (below 400 threshold)", flush=True)
                                else:
                                    mic_data = SILENCE
                            except Exception as me:
                                print(f">>> mic_stream.read error: {me}", flush=True)
                                mic_data = SILENCE
                            if sys_stream:
                                # System audio is BEST-EFFORT here — the mic (the user's
                                # voice on Space) is the primary path and must never be
                                # disrupted. We do NOT hot-swap on silence: silence is the
                                # normal case (user is speaking, nothing is playing), and
                                # churning through loopback devices on silence was both
                                # pointless and, on some setups, actively corrupted the mic
                                # stream. Instead we only track genuine read HANGS/errors,
                                # and if the chosen loopback proves unreliable we silently
                                # drop system audio for the rest of the session so it can
                                # never interfere with mic capture again.
                                try:
                                    raw = _read_stream_timeout(sys_stream, _sys_chunk_frames, 0.5, "SYS")
                                except Exception:
                                    raw = None

                                if raw is None:
                                    _sys_hang_count += 1
                                    data = mic_data
                                    if _sys_hang_count >= SYS_HANG_DISABLE_LIMIT:
                                        print(">>> SYSTEM AUDIO disabled for this session — its loopback "
                                              "device kept hanging. Running mic-only; your voice still "
                                              "transcribes normally.", flush=True)
                                        # Deliberately abandon WITHOUT closing: a hung read left a
                                        # daemon thread blocked inside the native stream.read(), and
                                        # closing it out from under that thread is a use-after-close
                                        # crash at the C level. Dropping the reference is safe.
                                        sys_stream = None
                                else:
                                    _sys_hang_count = 0
                                    sys_data = (resample_to_16k_mono(raw, _sys_native_rate, _sys_native_channels, num_frames)
                                                if (_sys_native_rate != SAMPLE_RATE or _sys_native_channels != 1)
                                                else raw)
                                    data = mix_audio(mic_data, sys_data)
                            else:
                                data = mic_data

                        # Handle recording — all state changes under record_lock so
                        # is_recording and recording_frames are always consistent.
                        # The save thread is started AFTER releasing the lock to
                        # avoid deadlock (save_recording also acquires record_lock).
                        if recording_requested:
                            with record_lock:
                                if not is_recording:
                                    is_recording = True
                                    active_recording_id = get_recording_id()
                                    print(">>> Recording started", flush=True)
                                if len(recording_frames) < MAX_RECORDING_FRAMES:
                                    recording_frames.append(data)
                        else:
                            stopped = stop_recording(shutdown_requested)
                            if not stopped:
                                mark_recording_saved(get_recording_id())

                        if shutdown_requested and not recording_requested:
                            raise RuntimeError("Shutdown requested")

                        return data

                class BufferedMixedStream:
                    """Continuously drain audio while Speechmatics completes its handshake.

                    The SDK deliberately waits for RecognitionStarted before asking the
                    stream for a chunk. Without this buffer, speech that begins right
                    after Space is pressed is discarded during the WebSocket handshake.
                    Only audio captured while listening is retained, so paused sessions
                    can never leak stale audio into the next question.
                    """

                    MAX_BUFFERED_CHUNKS = 120  # 12 seconds at the 100 ms capture cadence

                    def __init__(self, source):
                        self._source = source
                        self._chunks = deque()
                        self._condition = threading.Condition()
                        self._stopped = threading.Event()
                        self._capture_error = None
                        self._was_paused = True
                        self._thread = threading.Thread(
                            target=self._capture_loop,
                            name="speechmatics-audio-prebuffer",
                            daemon=True,
                        )
                        self._thread.start()

                    def _capture_loop(self):
                        while not self._stopped.is_set():
                            try:
                                # The source owns all PyAudio reads and recording state,
                                # preserving the existing capture behavior exactly once.
                                data = self._source.read(CHUNK_FRAMES, exception_on_overflow=False)
                            except Exception as ex:
                                with self._condition:
                                    self._capture_error = ex
                                    self._condition.notify_all()
                                return

                            paused = os.path.exists(PAUSE_FLAG)
                            with self._condition:
                                if paused:
                                    self._chunks.clear()
                                else:
                                    if self._was_paused:
                                        self._chunks.clear()
                                        print(
                                            ">>> PREBUFFER: preserving early speech while Speechmatics connects",
                                            flush=True,
                                        )
                                    if len(self._chunks) >= self.MAX_BUFFERED_CHUNKS:
                                        self._chunks.popleft()
                                    self._chunks.append(data)
                                    self._condition.notify_all()
                                self._was_paused = paused

                    def read(self, num_frames, exception_on_overflow=False):
                        with self._condition:
                            while not self._chunks:
                                if self._capture_error is not None:
                                    raise RuntimeError(
                                        f"audio prebuffer failed: {self._capture_error}"
                                    )
                                if self._stopped.is_set():
                                    return b""
                                self._condition.wait(timeout=0.25)
                            return self._chunks.popleft()

                    def close(self):
                        self._stopped.set()
                        with self._condition:
                            self._condition.notify_all()
                        # The source read has its own 0.5 s timeout, so this prevents a
                        # reconnect from ever leaving a second reader on the same device.
                        self._thread.join(timeout=0.75)

                buffered_stream = BufferedMixedStream(MixedStream())
                try:
                    await ws.run(buffered_stream, conf, audio_conf)
                finally:
                    buffered_stream.close()
                connected       = True
                reconnect_delay = 3
                print(f">>> Disconnected from {endpoint} cleanly.", flush=True)
                break

            except Exception as e:
                err = str(e)
                print(f">>> ERROR on {endpoint}: {err}", flush=True)

                # The newer model was refused. Drop to the older one and retry
                # immediately rather than working through the remaining endpoints
                # with a request every one of them will also refuse.
                if _downgrade_model_if_rejected(err):
                    continue

                # Auth failures: WebSocket sends {'type': 'not_authorised'} which
                # becomes "Not Authorized" in the exception message. Catch all variants.
                is_auth_error = (
                    "401" in err or
                    "not_authorised" in err.lower() or
                    "not authorized" in err.lower() or
                    err.strip().lower() in ("unauthorized", "not authorized")
                )
                if is_auth_error:
                    auth_errors += 1
                    # A key that's merely region-locked (e.g. an EU-only self-service
                    # key) gets rejected with this SAME "not_authorised" message on the
                    # wrong-region endpoint. Only treat it as a truly bad key once every
                    # endpoint has rejected it — otherwise we'd exit fatally on the first
                    # (wrong-region) endpoint and never reach the one that actually works.
                    print(f">>> Auth rejected on {endpoint} — trying next endpoint before giving up...", flush=True)
                    if auth_errors >= len(endpoints):
                        print(">>> FATAL: API key rejected on ALL endpoints. Check your Speechmatics key in Settings.", flush=True)
                        sys.exit(2)  # exit code 2 = auth failure (C# uses this to skip restart)
                    continue

                if "Audio Usage Exceeded" in err or "timelimit_exceeded" in err.lower():
                    print(">>> FATAL: AUDIO_USAGE_EXCEEDED. Speechmatics audio usage limit has been reached.", flush=True)
                    raise SystemExit(3)  # C# surfaces this as a non-retriable service-limit state.

                # A blocked contract looked like nothing at all. It is not an auth
                # failure and not a usage limit, so it fell through to "trying next
                # endpoint" and the engine retried on a doubling backoff forever
                # while the app showed "connecting":
                #
                #   'type': 'not_allowed',
                #   'reason': 'Contract blocked: Credit Balance Exhausted'
                #
                # Exiting as an auth failure is the useful thing to do, because the
                # token in hand was minted by the blocked account and will never
                # work again. That code makes the app throw the cached token away
                # and fetch a new one, which is exactly the right move: it fails
                # again while the contract is still blocked, and it recovers by
                # itself the moment billing is restored or the key is replaced.
                if ("contract blocked" in err.lower()
                        or "credit balance exhausted" in err.lower()):
                    print(">>> FATAL: SPEECHMATICS CONTRACT BLOCKED — " + err.strip()[:140], flush=True)
                    print(">>> The account has no credit. Transcription cannot run until "
                          "billing is restored or the key is replaced.", flush=True)
                    raise SystemExit(2)

                if "404" in err:
                    print(">>> 404 on this endpoint — SDK may be outdated.", flush=True)
                    print(">>> Run: pip install --upgrade speechmatics", flush=True)
                    continue

                print(">>> Trying next endpoint...", flush=True)

        if not connected:
            # Say so out loud. The app latched "online" on the first success and
            # had no way to learn otherwise, so a dropped session left it
            # believing transcription still worked while the user spoke into
            # nothing and got an empty question back.
            print(">>> STATUS: OFFLINE", flush=True)
            print(f">>> All endpoints failed. Retrying in {reconnect_delay}s...", flush=True)
            await asyncio.sleep(reconnect_delay)
            reconnect_delay = min(reconnect_delay * 2, 60)


# ── ENTRY POINT ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    asyncio.run(main())
