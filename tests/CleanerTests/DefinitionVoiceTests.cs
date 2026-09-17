using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// "What is Java?" must sound like the candidate, and must never claim a tool
/// they do not have.
///
/// Session 418 got "Java lets you write code that runs on any platform with a
/// JVM", which the owner read as a textbook. Starting from the candidate's own
/// work fixes the tone, but the model could not be trusted to check the resume:
/// it said "Rust's the language I use" for a candidate with no Rust. So the app
/// checks, and these cases pin that check.
/// </summary>
internal static class DefinitionVoiceTests
{
    internal static int Run()
    {
        int failed = 0;

        void Case(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; if (detail.Length > 0) Console.WriteLine($"        {detail}"); }
        }

        const string facts = "Pavan Krishna, Gen AI Engineer at UHG. Skills: Python, Java, Spring Boot, " +
                             "Kafka, PostgreSQL, Docker, Kubernetes, REST APIs, RAG pipelines. Ready to go live.";

        // Term extraction
        Case("term: What is Java?", PromptBuilder.DefinitionTerm("What is Java?") == "Java", PromptBuilder.DefinitionTerm("What is Java?"));
        Case("term: What is a REST API?", PromptBuilder.DefinitionTerm("What is a REST API?") == "REST API", PromptBuilder.DefinitionTerm("What is a REST API?"));
        Case("term: speech with no punctuation", PromptBuilder.DefinitionTerm("what is kafka exactly") == "kafka", PromptBuilder.DefinitionTerm("what is kafka exactly"));
        Case("term: define", PromptBuilder.DefinitionTerm("Define the CAP theorem.") == "CAP theorem", PromptBuilder.DefinitionTerm("Define the CAP theorem."));

        // On the resume: may say where it sits in their work
        Case("Java is in the facts", PromptBuilder.FactsMention(facts, "Java"));
        Case("lowercase speech still matches", PromptBuilder.FactsMention(facts, "kafka"));
        Case("Spring Boot matches as a phrase", PromptBuilder.FactsMention(facts, "Spring Boot"));
        Case("REST API matches REST APIs", PromptBuilder.FactsMention(facts, "REST API"));
        Case("RAG matches", PromptBuilder.FactsMention(facts, "RAG"));

        // Not on the resume: must never claim use
        Case("Rust is not in the facts", !PromptBuilder.FactsMention(facts, "Rust"));
        Case("Terraform is not in the facts", !PromptBuilder.FactsMention(facts, "Terraform"));
        Case("JavaScript is not Java", !PromptBuilder.FactsMention(facts, "JavaScript"));
        Case("Go is not 'go' in ordinary prose", !PromptBuilder.FactsMention(facts, "Go"));
        Case("Spring alone is not Spring Batch", !PromptBuilder.FactsMention(facts, "Spring Batch"));
        Case("no resume never claims use", !PromptBuilder.FactsMention("No resume provided.", "Java"));
        Case("empty resume never claims use", !PromptBuilder.FactsMention("", "Java"));

        // The reminder picked from that
        string onResume = PromptBuilder.DefinitionReminder("Java", facts);
        string offResume = PromptBuilder.DefinitionReminder("Rust", facts);
        Case("on-resume reminder starts from their work", onResume.Contains("Java is in the verified facts") && onResume.Contains("sits in your work"));
        Case("off-resume reminder forbids claiming use", offResume.Contains("Rust is NOT in the verified facts") && offResume.Contains("do not say you use it"));
        Case("both ban the textbook opening", onResume.Contains("TERM lets you") && offResume.Contains("TERM is a NOUN"));

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "definition voice: all passed" : $"definition voice: {failed} FAILED");
        return failed;
    }
}
