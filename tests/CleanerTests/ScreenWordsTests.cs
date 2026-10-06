using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The words on a screen, laid out for reading. The reader returns words with positions; a problem on the left and an
/// editor on the right share every row, so read straight across they interleave. Panels are read one after the other,
/// each top to bottom, and indentation survives.
/// </summary>
internal static class ScreenWordsTests
{
    private static OcrWord W(string text, double x, double y) => new(text, x, y, text.Length * 8, 16);

    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // A page of one column reads in rows, left to right within a row.
        var single = new List<OcrWord>();
        for (int i = 0; i < 8; i++)
        {
            single.Add(W("Second", 60, 20 * i));
            single.Add(W($"first{i}", 0, 20 * i));
        }
        string[] singleLines = OcrLayout.ToText(single).Split('\n');
        Check(singleLines.Length == 8 && singleLines[0].StartsWith("first0 ") && singleLines[0].EndsWith("Second") && singleLines[7].StartsWith("first7"),
            "one column: rows top to bottom, the words of a row left to right");

        // Two panels sharing every row: all of the left, then all of the right, never interleaved.
        var two = new List<OcrWord>();
        for (int i = 0; i < 10; i++)
        {
            two.Add(W($"left{i}row", 0, 20 * i));
            two.Add(W("more", 80, 20 * i));
            two.Add(W($"right{i}", 400, 20 * i));
        }
        string spread = OcrLayout.ToText(two);
        string[] parts = spread.Split("\n\n");
        Check(parts.Length == 2 && parts[0].Split('\n').All(l => l.StartsWith("left")) && parts[1].Split('\n').All(l => l.StartsWith("right")),
            "two panels side by side: the left one is read down, then the right one");
        Check(spread.IndexOf("left9row") < spread.IndexOf("right0"),
            "the end of the left panel comes before the start of the right panel");

        // A title bar whose words cross the gutter does not stop the split.
        var withTitle = new List<OcrWord>(two) { W("Practice - live session title that runs across", 100, -40) };
        string[] titled = OcrLayout.ToText(withTitle).Split("\n\n");
        Check(titled.Length >= 2 && titled[^1].Split('\n').Last().StartsWith("right9"),
            "a title bar across the gutter does not merge the panels");

        // Indentation, measured from the panel's left edge in characters.
        var code = new List<OcrWord>();
        double[] xs = { 0, 32, 64, 32, 0, 0 };
        for (int i = 0; i < xs.Length; i++) code.Add(W($"stmt{i}", xs[i], 20 * i));
        string[] codeLines = OcrLayout.ToText(code).Split('\n');
        Check(codeLines[0] == "stmt0" && codeLines[1] == "    stmt1" && codeLines[2] == "        stmt2" && codeLines[4] == "stmt4",
            "indentation is kept, four characters for each 32 pixels");

        // A gap inside a row is a space, a wide one a few.
        var gapped = new List<OcrWord>();
        for (int i = 0; i < 6; i++) { gapped.Add(W("abc", 0, 20 * i)); gapped.Add(W("def", 24 + 16, 20 * i)); }
        Check(OcrLayout.ToText(gapped).Split('\n')[0] == "abc  def", "a gap of two characters between words is two spaces");

        Check(OcrLayout.ToText(new List<OcrWord>()) == "", "nothing found reads as nothing");
        Check(OcrLayout.ToText(new List<OcrWord> { new("   ", 0, 0, 10, 10) }) == "", "blank words are not text");

        // Cut at a line end, never mid-word.
        string longText = string.Join("\n", Enumerable.Repeat("a line of the page that is fairly long", 1000));
        string fitted = ScreenOcr.Fit(longText);
        Check(fitted.Length <= ScreenOcr.MaxChars && fitted.EndsWith("long"), "a very long page is cut at the end of a line");
        Check(ScreenOcr.Fit("short") == "short", "a short page is untouched");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "screen words: all passed" : $"screen words: {failed} FAILED");
        return failed;
    }
}
