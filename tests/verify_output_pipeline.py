import io, re

sa = io.open(r'C:\Users\krish\Desktop\windowsNative\ScreenAnalyzer.cs', encoding='utf-8-sig').read()
mw = io.open(r'C:\Users\krish\Desktop\windowsNative\MainWindow.xaml.cs', encoding='utf-8-sig').read()

def check(label, ok, detail=""):
    print(("PASS  " if ok else "FAIL  ") + label + ("" if ok else "   << " + detail))

# 1. No raw control characters anywhere (they break editors, diffs and encodings).
for name, src in (("ScreenAnalyzer.cs", sa), ("MainWindow.xaml.cs", mw)):
    bad = [i+1 for i, l in enumerate(src.split('\n'))
           if any(ord(c) < 9 or (13 < ord(c) < 32) for c in l)]
    check("%s free of raw control chars" % name, not bad, "lines %s" % bad[:5])

# 2. The shared helper exists and is public.
check("TransformProseOnly is shared/public",
      "public static string TransformProseOnly" in sa)

# 3. Neither dangerous rule runs unprotected on a whole answer any more.
#    (i.e. every emphasis-stripping regex sits inside a TransformProseOnly lambda)
for name, src in (("ScreenAnalyzer.PostProcess", sa), ("MainWindow.CleanAiOutput", mw)):
    unprotected = re.findall(r'^\s*(?:ans|raw)\s*=\s*Regex\.Replace\((?:ans|raw),\s*@"\\\*', src, re.M)
    check("%s: no unprotected asterisk rule" % name, not unprotected,
          "%d found" % len(unprotected))
    unprotected_us = re.findall(r'^\s*(?:ans|raw)\s*=\s*Regex\.Replace\((?:ans|raw),\s*@"_', src, re.M)
    check("%s: no unprotected underscore rule" % name, not unprotected_us,
          "%d found" % len(unprotected_us))

# 4. Both cleaners actually call the helper.
check("PostProcess routes through helper", "TransformProseOnly(raw" in sa)
check("CleanAiOutput routes through helper", "ScreenAnalyzer.TransformProseOnly(ans" in mw)

# 5. F8 path now uses the code panel.
check("F8 screen path calls ShowAnswer", "ShowAnswer(finalResult" in mw)
