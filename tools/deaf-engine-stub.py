"""
A speech engine that hears everything and returns nothing.

This exists to make the deafness warning appear on demand. The failure it
imitates is the one that actually happened three times: the engine stays
connected, reports itself online, the microphone level moves - and no words
ever come back. The app looked healthy every time, so the user assumed they
were doing something wrong, and the interview was over before anyone worked
it out.

SpeechHealthTests proves the decision is right. This proves the wiring is:
that the lines the real engine prints are the lines the app is watching for,
and that the warning reaches the screen. A rule that is correct and a rule
that is connected are different claims, and only one of them can be checked
without running the app.

HOW TO USE IT

  1. Build the app, then in the build output folder rename engine\ to
     engine.off so the bundled engine is skipped.
  2. Copy this file over speechmatics_engine.py in the same folder.
  3. Start the app and begin listening as normal.
  4. Within about twelve seconds the app should say
     HEARING YOU BUT NOT TRANSCRIBING - RESTART THE APP
     and write the same thing to the debug log.
  5. Put engine\ and the real speechmatics_engine.py back.

If the warning does NOT appear, the detector is not wired to what the engine
actually prints, and the unit tests will not tell you that.
"""

import sys
import time

# Whatever it is asked for, it starts and claims to be healthy.
print(">>> ENGINE BUILD: deaf-stub (not a real engine)", flush=True)
print("STATUS: ONLINE", flush=True)
print(">>> Speechmatics session started", flush=True)

# From here it behaves exactly like an engine whose audio reader is broken:
# the microphone is clearly live, and nothing is ever transcribed.
#
# Deliberately never prints "PARTIAL received" or "FINAL received". That pair
# is the only thing separating this from a quiet room, which is why the app
# watches for it rather than for silence.
start = time.time()
while True:
    print("MIC SIGNAL DETECTED", flush=True)
    elapsed = int(time.time() - start)
    if elapsed and elapsed % 10 == 0:
        print(f">>> stub: {elapsed}s of speech heard, 0 words returned", flush=True)
    time.sleep(1)
