using System.Text.Json;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A loaded resume must reach the model with its real responsibilities and
/// projects. Prompt-writing examples must never seed an unrelated career.
/// </summary>
internal static class ResumeGroundingTests
{
    internal static int Run()
    {
        int failed = 0;

        void Case(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; if (detail.Length > 0) Console.WriteLine($"        {detail}"); }
        }

        const string resume = """
            Tandrita Dey
            Machine Learning Engineer
            tandrita@example.com | +91 98765 43210 | linkedin.com/in/tandrita

            EXPERIENCE
            Machine Learning Engineer     May 2024 - Present
            Northstar Analytics
            • Built computer-vision pipelines for document classification using PyTorch.
            • Designed feature stores and model-monitoring dashboards for production inference.

            PROJECTS
            Claims anomaly detection
            Trained an isolation-forest workflow and documented false-positive trade-offs.

            SKILLS
            PyTorch, scikit-learn, pandas, computer vision, model monitoring

            EDUCATION
            Master of Science in Data Science
            """;

        string facts = ResumeParser.ExtractFacts(resume);
        Case("real responsibility survives resume extraction",
            facts.Contains("Built computer-vision pipelines", StringComparison.Ordinal));
        Case("project detail survives resume extraction",
            facts.Contains("Claims anomaly detection", StringComparison.Ordinal) &&
            facts.Contains("false-positive trade-offs", StringComparison.Ordinal));
        Case("education survives resume extraction",
            facts.Contains("Master of Science in Data Science", StringComparison.Ordinal));
        Case("email is removed from model context", !facts.Contains("tandrita@example.com", StringComparison.Ordinal));
        Case("phone is removed from model context", !facts.Contains("98765 43210", StringComparison.Ordinal));
        Case("profile URL is removed from model context", !facts.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase));

        PromptBuilder.ClearHistory();
        PromptBuilder.SetContext("", "", "");
        string payload = JsonSerializer.Serialize(
            PromptBuilder.BuildMessages(facts, "Tell me about yourself"));

        Case("actual resume evidence reaches the request",
            payload.Contains("computer-vision pipelines", StringComparison.Ordinal) &&
            payload.Contains("model-monitoring dashboards", StringComparison.Ordinal));
        Case("prompt does not seed a dispatch-system career",
            !payload.Contains("dispatch system", StringComparison.OrdinalIgnoreCase));
        Case("prompt does not seed an unrelated employer",
            !payload.Contains("Uber", StringComparison.OrdinalIgnoreCase));
        Case("prompt does not seed Java or Spring Boot",
            !payload.Contains("Java", StringComparison.OrdinalIgnoreCase) &&
            !payload.Contains("Spring Boot", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "resume grounding: all passed" : $"resume grounding: {failed} FAILED");
        return failed;
    }
}
