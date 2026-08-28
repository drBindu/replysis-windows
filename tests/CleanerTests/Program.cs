using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Feeds real answers through the real cleaner and compares bytes.
///
/// The question this asks is "does the shipping cleaner damage code", and it
/// asks it of the shipping cleaner rather than of a description of it. The
/// version of this idea that came before greps the C# for a function name and
/// confirms the call exists; it was green for the whole period the cleaner was
/// corrupting every unfenced line it was given, because a call being wired up
/// says nothing about what comes out of it.
/// </summary>
internal static class Program
{
    private static int _failed;

    private static void Check(bool ok, string label, string? detail = null)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
        if (!ok)
        {
            _failed++;
            if (detail != null) Console.WriteLine($"        {detail}");
        }
    }

    /// <summary>Lines that must arrive byte-identical.</summary>
    private static readonly string[] Code =
    {
        // The line this all started from. Pasted into LeetCode it gave
        // "invalid argument type 'ListNode' to unary expression", over and
        // over, while the server log showed the code leaving correct.
        "ListNode* insertionSortList(ListNode* head) {",
        "int *a, *b;",
        "*dst = *src;",
        "void f(char **argv, int *n)",
        "def f(*args, **kwargs):",            // never was a pointer
        "area = w * h * depth;",              // nor was this
        "/* copy */ memcpy(dst, *src, n);",
        "user_name = get_user_name(user_id)", // the underscore rule's reach
        "std::vector<int>* p = &v;",
        "a *= 2; b **= 3;",
        "x = y_1 * z_2;",
        "p = *q++;",
        "__init__ and __repr__",
    };

    private static readonly (string Given, string Want)[] Markdown =
    {
        ("This is **important** to say",   "This is important to say"),
        ("Use *emphasis* sparingly",       "Use emphasis sparingly"),
        ("The _key_ idea",                 "The key idea"),
        ("Both **bold** and *italic* here","Both bold and italic here"),
        ("__strong__ text",                "strong text"),
    };

    /// <summary>
    /// Dunders in ordinary prose, nowhere near a code section. Held by an
    /// exact list, because no shape test separates __init__ from __strong__.
    /// </summary>
    private static readonly string[] ProseDunders =
    {
        "You override __init__ to set that up.",
        "Define __enter__ and __exit__ for the context manager.",
        "The __name__ == __main__ guard stops it running on import.",
        "__len__ makes len() work on your own type.",
    };

    private static int Main()
    {
        Console.WriteLine("1. Code inside a fence");
        foreach (string c in Code)
            Check(ScreenAnalyzer.PostProcess($"```python\n{c}\n```").Contains(c), c);

        Console.WriteLine("\n2. Code under a bare SOLUTION heading, no fence");
        string body = "APPROACH\nTwo pointers.\nSOLUTION\n"
                    + string.Join("\n", Code) + "\nCOMPLEXITY\nTime: O(n)\n";
        string got = ScreenAnalyzer.PostProcess(body);
        foreach (string c in Code) Check(got.Contains(c), c);

        Console.WriteLine("\n3. Markdown in prose is still removed");
        foreach (var (given, want) in Markdown)
        {
            string g = ScreenAnalyzer.PostProcess(given).Trim();
            Check(g == want, given, $"got \"{g}\" want \"{want}\"");
        }

        Console.WriteLine("\n4. Dunders in plain prose, no code section");
        foreach (string line in ProseDunders)
        {
            string g = ScreenAnalyzer.PostProcess(line).Trim();
            Check(g == line, line, $"got {g}");
        }

        Console.WriteLine("\n5. A whole answer survives a round trip");
        string answer =
            "SAY THIS\n" +
            "I'd sort it with **merge sort** to keep it stable.\n" +
            "APPROACH\n" +
            "Insertion sort on a linked list, *in place*.\n" +
            "SOLUTION\n" +
            "ListNode* insertionSortList(ListNode* head) {\n" +
            "    ListNode *dummy = new ListNode(0), *curr = head;\n" +
            "    while (curr) { ListNode* next = curr->next; }\n" +
            "    return dummy->next;\n" +
            "}\n" +
            "COMPLEXITY\n" +
            "Time: O(n^2)   Space: O(1)\n";
        string outp = ScreenAnalyzer.PostProcess(answer);
        Check(outp.Contains("ListNode* insertionSortList(ListNode* head) {"), "pointer signature intact");
        Check(outp.Contains("ListNode *dummy = new ListNode(0), *curr = head;"), "declaration intact");
        Check(outp.Contains("ListNode* next = curr->next;"), "inner pointer intact");
        Check(outp.Contains("merge sort") && !outp.Contains("**merge sort**"), "prose bold stripped");
        Check(outp.Contains("in place") && !outp.Contains("*in place*"), "prose italic stripped");

        Console.WriteLine("\n6. The deafness detector fires, and stays quiet");
        _failed += SpeechHealthTests.Run();

        Console.WriteLine("\n7. What a listening session costs");
        _failed += BillingTests.Run();

        Console.WriteLine("\n8. Background chatter is not a continuation");
        _failed += ContinuationTests.Run();

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine(_failed == 0 ? "all passed" : $"{_failed} FAILED");
        return _failed == 0 ? 0 : 1;
    }
}

