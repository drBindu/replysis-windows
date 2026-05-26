import os
import argparse
import asyncio
import pyaudio
import wave
import threading
import tempfile
import numpy as np

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

        test_stream = p_test.open(
            format=pyaudio.paInt16, channels=1, rate=16000,
            input=True, frames_per_buffer=4096
        )
        has_audio = False
        for _ in range(10):
            data = test_stream.read(4096, exception_on_overflow=False)
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
            # VB-Cable shows up as "CABLE Output" as an input device
            if 'cable output' in name or 'vb-audio' in name or 'vb cable' in name:
                print(f">>> VB-Cable found: [{i}] {info['name']}", flush=True)
                return i
    print(">>> VB-Cable NOT found. Install from vb-audio.com for system audio capture.", flush=True)
    return None


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
    exit(1)


# ── ARGS ──────────────────────────────────────────────────────────────────────
parser = argparse.ArgumentParser()
parser.add_argument("--device",     type=int,   default=None,
                    help="PyAudio input device index for MIC")
parser.add_argument("--sysdevice",  type=int,   default=None,
                    help="PyAudio input device index for system audio (VB-Cable). Auto-detected if not set.")
parser.add_argument("--max-delay",  type=float, default=5.0)
args = parser.parse_args()

# Read API key from environment variable (avoids exposing it in process arguments)
_env_key = os.environ.get("SM_API_KEY", "")
if not _env_key:
    print(">>> FATAL: SM_API_KEY environment variable not set.", flush=True)
    exit(1)
args.key = _env_key


# ── PATHS ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

APP_DATA = os.path.join(
    os.environ.get("LOCALAPPDATA", tempfile.gettempdir()),
    "InterviewCopilot"
)
os.makedirs(APP_DATA, exist_ok=True)

LATEST_FILE    = os.path.join(APP_DATA, "latest.txt")
PAUSE_FLAG     = os.path.join(APP_DATA, "pause.flag")
RESET_FLAG     = os.path.join(APP_DATA, "reset.flag")
RECORD_FLAG    = os.path.join(APP_DATA, "record.flag")
SHUTDOWN_FLAG  = os.path.join(APP_DATA, "shutdown.flag")
RECORDINGS_DIR = APP_DATA

print(f">>> Script folder : {SCRIPT_DIR}", flush=True)
print(f">>> Data folder   : {APP_DATA}", flush=True)
print(">>> API key       : ********...", flush=True)


# ── RUN TESTS ─────────────────────────────────────────────────────────────────
mic_ok = test_microphone()
if not mic_ok:
    print(">>> FATAL: Microphone unavailable. Exiting.", flush=True)
    exit(1)


# ── RECORDING STATE ───────────────────────────────────────────────────────────
recording_frames = []
is_recording     = False
record_lock      = threading.Lock()

def save_recording():
    # Frames are already snapshot-copied by the caller before this thread starts,
    # so no lock is needed here.
    global recording_frames
    with record_lock:
        if not recording_frames:
            return
        frames_to_save   = recording_frames[:]
        recording_frames = []

    n = 1
    while os.path.exists(os.path.join(RECORDINGS_DIR, f"interview_{n}.wav")):
        n += 1
    filename = os.path.join(RECORDINGS_DIR, f"interview_{n}.wav")
    wf = None
    try:
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


# ── PYAUDIO STREAMS ───────────────────────────────────────────────────────────
CHUNK_FRAMES = 4096
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


try:
    p = pyaudio.PyAudio()
    write_devices_file(p)

    # ── MIC STREAM ──
    mic_kwargs = dict(
        format=pyaudio.paInt16,
        channels=1,
        rate=SAMPLE_RATE,
        input=True,
        frames_per_buffer=CHUNK_FRAMES,
    )
    if args.device is not None:
        mic_kwargs["input_device_index"] = args.device
        dev_name = p.get_device_info_by_index(args.device)['name']
        print(f">>> MIC device [{args.device}]: {dev_name}", flush=True)
    else:
        print(">>> MIC: using default input device", flush=True)

    mic_stream = p.open(**mic_kwargs)
    print(">>> MIC stream opened OK", flush=True)

    # ── SYSTEM AUDIO STREAM (VB-Cable) ──
    sys_device_index = args.sysdevice
    if sys_device_index is None:
        sys_device_index = find_vbcable_device(p)

    sys_stream = None
    if sys_device_index is not None:
        try:
            sys_kwargs = dict(
                format=pyaudio.paInt16,
                channels=1,
                rate=SAMPLE_RATE,
                input=True,
                input_device_index=sys_device_index,
                frames_per_buffer=CHUNK_FRAMES,
            )
            sys_stream = p.open(**sys_kwargs)
            sys_name = p.get_device_info_by_index(sys_device_index)['name']
            print(f">>> SYSTEM AUDIO stream opened: [{sys_device_index}] {sys_name}", flush=True)
        except Exception as e:
            print(f">>> WARNING: Could not open system audio stream: {e}", flush=True)
            print(">>> Falling back to mic only.", flush=True)
            sys_stream = None
    else:
        print(">>> SYSTEM AUDIO: not available (mic only mode)", flush=True)

except Exception as e:
    print(f">>> FATAL: Cannot open audio stream - {e}", flush=True)
    exit(1)

SILENCE = b"\x00" * (CHUNK_FRAMES * 2)


def mix_audio(mic_data: bytes, sys_data: bytes) -> bytes:
    """
    Mix mic + system audio by averaging. Clamps to int16 range.
    Both inputs must be same length PCM S16LE mono.
    """
    mic_arr = np.frombuffer(mic_data, dtype=np.int16).astype(np.int32)
    sys_arr = np.frombuffer(sys_data, dtype=np.int16).astype(np.int32)
    mixed   = ((mic_arr + sys_arr) // 2).clip(-32768, 32767).astype(np.int16)
    return mixed.tobytes()


# ── TRANSCRIPT TEXT BUILDER ───────────────────────────────────────────────────
def build_text_from_results(results):
    text = ""
    for res in results:
        if not res.get("alternatives"):
            continue
        word    = res["alternatives"][0]["content"]
        is_punc = res.get("type") == "punctuation"
        if is_punc:
            text = text.rstrip() + word + " "
        else:
            text += word + " "
    return text


# ── MAIN WITH AUTO-RECONNECT ──────────────────────────────────────────────────
async def main():
    print("", flush=True)
    print("===============================================", flush=True)
    print("   SPEECHMATICS ENGINE: READY", flush=True)
    if sys_stream:
        print("   MODE: MIC + SYSTEM AUDIO (mixed)", flush=True)
    else:
        print("   MODE: MIC ONLY (install VB-Cable for system audio)", flush=True)
    print(f"   max_delay={args.max_delay}s  chunk={CHUNK_FRAMES}frames", flush=True)
    print("===============================================", flush=True)

    endpoints = [
        "wss://us.rt.speechmatics.com/v2",
        "wss://eu.rt.speechmatics.com/v2",
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

        for endpoint in endpoints:
            try:
                print(f">>>[Attempt {attempt}] Connecting to {endpoint}...", flush=True)

                settings = ConnectionSettings(
                    url        = endpoint,
                    auth_token = args.key.strip(),
                )

                ws = WebsocketClient(settings)

                confirmed_text = ""
                partial_text   = ""

                def handle_final(msg):
                    nonlocal confirmed_text, partial_text

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
                    print(f">>> FINAL: {display}", flush=True)
                    _write(display)

                def handle_partial(msg):
                    nonlocal partial_text

                    if os.path.exists(PAUSE_FLAG):
                        return
                    if os.path.exists(RESET_FLAG):
                        try: os.remove(RESET_FLAG)
                        except: pass
                        return

                    segment      = build_text_from_results(msg.get("results", []))
                    partial_text = segment

                    display = (confirmed_text + partial_text).strip()
                    print(f">>> PARTIAL: {display}", flush=True)
                    _write(display)

                def _write(text):
                    try:
                        with open(LATEST_FILE, "w", encoding="utf-8") as f:
                            f.write(text)
                    except Exception as fe:
                        print(f">>> File write error: {fe}", flush=True)

                ws.add_event_handler("AddTranscript",        handle_final)
                ws.add_event_handler("AddPartialTranscript", handle_partial)
                ws.add_event_handler("RecognitionStarted",
                                     lambda m: print(">>> STATUS: ONLINE ✓", flush=True))
                ws.add_event_handler("Error",
                                     lambda e: print(f">>> WS ERROR: {e}", flush=True))

                conf = TranscriptionConfig(
                    language        = "en",
                    operating_point = "enhanced",
                    max_delay       = args.max_delay,
                    enable_partials = True,
                    punctuation_overrides = {
                        "permitted_marks": [".", ",", "?", "!"],
                        "sensitivity"    : 0.5,
                    },
                    enable_entities = True,
                    disfluencies    = False,
                    additional_vocab = [
                        {"content": "AVA",              "sounds_like": ["AY-VAH", "AY-VA"]},
                        {"content": "AVA Inc"},
                        {"content": "HPLC",             "sounds_like": ["H-P-L-C"]},
                        {"content": "FTIR",             "sounds_like": ["F-T-I-R"]},
                        {"content": "GMP"},
                        {"content": "GC"},
                        {"content": "ALCOA"},
                        {"content": "LIMS"},
                        {"content": "OOS"},
                        {"content": "OOT"},
                        {"content": "CAPA"},
                        {"content": "IQ/OQ/PQ"},
                        {"content": "Waters Empower"},
                        {"content": "Agilent ChemStation"},
                        {"content": "Willowbrook"},
                    ]
                )

                audio_conf = AudioSettings(
                    encoding    = "pcm_s16le",
                    sample_rate = SAMPLE_RATE,
                    chunk_size  = CHUNK_FRAMES,
                )

                class MixedStream:
                    def read(self, num_frames, exception_on_overflow=False):
                        global is_recording, recording_frames

                        if os.path.exists(PAUSE_FLAG):
                            try:
                                mic_stream.read(num_frames, exception_on_overflow=False)
                                if sys_stream:
                                    sys_stream.read(num_frames, exception_on_overflow=False)
                            except:
                                pass
                            return SILENCE

                        # Read mic
                        mic_data = mic_stream.read(num_frames, exception_on_overflow=False)

                        # Read system audio and mix if available
                        if sys_stream:
                            try:
                                sys_data = sys_stream.read(num_frames, exception_on_overflow=False)
                                data = mix_audio(mic_data, sys_data)
                            except:
                                data = mic_data  # fallback to mic only if sys fails
                        else:
                            data = mic_data

                        # Handle recording — all state changes under record_lock so
                        # is_recording and recording_frames are always consistent.
                        # The save thread is started AFTER releasing the lock to
                        # avoid deadlock (save_recording also acquires record_lock).
                        _start_save = False
                        with record_lock:
                            if os.path.exists(RECORD_FLAG):
                                if not is_recording:
                                    is_recording = True
                                    print(">>> Recording started", flush=True)
                                recording_frames.append(data)
                            elif is_recording:
                                is_recording = False
                                _start_save = True
                        if _start_save:
                            print(">>> Recording stopped - saving...", flush=True)
                            threading.Thread(target=save_recording, daemon=True).start()

                        return data

                await ws.run(MixedStream(), conf, audio_conf)
                connected       = True
                reconnect_delay = 3
                print(f">>> Disconnected from {endpoint} cleanly.", flush=True)
                break

            except Exception as e:
                err = str(e)
                print(f">>> ERROR on {endpoint}: {err}", flush=True)

                if ("401" in err and ("Unauthorized" in err or "unauthorized" in err.lower() or "authentication" in err.lower())) or err.strip() == "Unauthorized":
                    print(">>> FATAL: API key rejected (401). Check your Speechmatics key.", flush=True)
                    exit(1)

                if "Audio Usage Exceeded" in err or "usage" in err.lower():
                    print(">>> Usage limit hit — trying next region...", flush=True)
                    continue
                if "404" in err:
                    print(">>> 404 on this endpoint — SDK may be outdated.", flush=True)
                    print(">>> Run: pip install --upgrade speechmatics", flush=True)
                    continue

                print(">>> Trying next endpoint...", flush=True)

        if not connected:
            print(f">>> All endpoints failed. Retrying in {reconnect_delay}s...", flush=True)
            await asyncio.sleep(reconnect_delay)
            reconnect_delay = min(reconnect_delay * 2, 60)


# ── ENTRY POINT ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    asyncio.run(main())
