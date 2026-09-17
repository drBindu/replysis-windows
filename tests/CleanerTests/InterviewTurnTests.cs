using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Turns taken from two real interviews, replayed against the classifier.
///
/// "Can you tell me about the RESTful services you built?" was a yes/no question
/// and got one sentence. "How do you see a role like this fitting into that path?"
/// went to the screen reader, which answered with template text. "What is cap
/// extension?" was explained like a technical term. "What are your strengths?"
/// was given the "why this company" instructions.
/// </summary>
internal static class InterviewTurnTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }
        PromptBuilder.QuestionType T(string q) { PromptBuilder.ClearHistory(); return PromptBuilder.DetectType(q); }

        // Polite requests are not yes/no questions
        Check(T("Can you tell me the RESTful services you did?") != PromptBuilder.QuestionType.YesNo, "'Can you tell me the RESTful services' is a request");
        Check(T("Can you please describe about your past experience?") == PromptBuilder.QuestionType.Intro, "'Can you please describe your past experience' is the introduction");
        Check(T("Could you walk me through your background?") == PromptBuilder.QuestionType.Intro, "'Could you walk me through your background' is the introduction");
        Check(T("Can you explain how Kafka guarantees ordering?") == PromptBuilder.QuestionType.Technical, "'Can you explain how Kafka...' is technical");
        Check(T("Can you tell me about a time you handled a production outage?") == PromptBuilder.QuestionType.Behavioral, "'Can you tell me about a time' is a story");
        Check(T("Can you start next week?") == PromptBuilder.QuestionType.Availability || T("Can you start next week?") == PromptBuilder.QuestionType.YesNo, "'Can you start next week?' stays short");
        Check(T("Are you authorized to work in the US?") == PromptBuilder.QuestionType.YesNo, "work authorization stays yes/no");

        // Visa questions are answered from the profile, not explained
        Check(T("what is cap extension?") == PromptBuilder.QuestionType.YesNo, "'what is cap extension' is a work status question");
        Check(T("Will you need H-1B sponsorship in the future?") == PromptBuilder.QuestionType.YesNo, "H-1B sponsorship is a work status question");
        Check(!PromptBuilder.IsWorkAuthorizationQuestion("how do you optimize a query"), "'optimize' is not OPT");
        Check(!PromptBuilder.IsWorkAuthorizationQuestion("who would you lead"), "'lead' is not EAD");

        // Screen reading only when something on screen is meant
        Check(!PromptBuilder.RefersToScreen("And how do you see a role like this fitting into that path?"), "'how do you see a role' is not about the screen");
        Check(!PromptBuilder.RefersToScreen("Where do you see yourself in five years?"), "'where do you see yourself' is not about the screen");
        Check(PromptBuilder.RefersToScreen("Can you see this code?"), "'can you see this code' is about the screen");
        Check(PromptBuilder.RefersToScreen("what do you see here"), "'what do you see here' is about the screen");
        Check(PromptBuilder.RefersToScreen("can you look at my screen"), "'my screen' is about the screen");

        // Collaboration is situational, strengths keep their own format
        Check(T("How do you approach working collaboratively with researchers on model development?") == PromptBuilder.QuestionType.Situational, "collaborating with researchers is situational");
        Check(T("And how do you see a role like this fitting into that path?") == PromptBuilder.QuestionType.General, "career direction is not a technical explanation");
        Check(T("What are your strengths?") == PromptBuilder.QuestionType.WhyRole, "strengths still detected");

        // After the candidate has asked their question, every way of asking
        // "anything else?" gets a short local reply, never another question.
        string[] followUps =
        {
            "Anything else?", "Anything else you'd like to ask?", "Any other questions?", "Do you have any other questions for me?",
            "Is there anything else you want to know?", "What else would you like to know about the role or the team?",
            "Any more questions?", "Sure. Anything else?", "Okay, any other questions for us?", "Is there something else you'd like to ask me?",
            "Do you have any more questions about the team", "Anything else I can answer for you?", "Any final questions?",
            "Is there another angle on the role, the tech, or the team that you'd like me to focus on?", "Does that answer your question?",
            "Did that help?", "Was that clear?", "Anything more?", "Do you want to ask anything else?", "Any follow-up questions?", "Any other thing you want to ask?",
        };
        void InPhase()
        {
            PromptBuilder.ClearHistory();
            PromptBuilder.AddToHistory("Before we wrap up, is there anything you'd like to ask me about the role or the team?",
                "What is the team's top priority for the next six months?");
            PromptBuilder.AddToHistory("Mainly scaling the platform, measured by adoption and serving latency.", "Thanks, that is helpful to hear.");
        }
        foreach (string f in followUps)
        {
            InPhase();
            bool local = PromptBuilder.TryGetClosingResponse(f, out string reply);
            Check(local && !reply.Contains('?'), $"after the candidate's question, '{f}' gets a short reply with no new question");
        }

        // The replies do not repeat word for word
        InPhase();
        PromptBuilder.TryGetClosingResponse("Anything else?", out string first);
        PromptBuilder.AddToHistory("Anything else?", first);
        PromptBuilder.TryGetClosingResponse("Any more questions?", out string second);
        Check(first != second, "two wrap-up replies in a row are not the same sentence");

        // Before any invitation, these are ordinary turns
        PromptBuilder.ClearHistory();
        Check(!PromptBuilder.TryGetClosingResponse("Anything else?", out _), "'Anything else?' mid-interview is not a wrap-up");
        Check(!PromptBuilder.TryGetClosingResponse("Did that help?", out _), "'Did that help?' mid-interview is not a wrap-up");
        Check(!PromptBuilder.TryGetClosingResponse("Do you have any questions for me?", out _), "the first invitation still lets the candidate ask one question");
        InPhase();
        Check(!PromptBuilder.TryGetClosingResponse("Anything else? How would you scale the serving layer?", out _), "a real question after 'anything else' still gets answered");
        Check(!PromptBuilder.TryGetClosingResponse("Any questions on the approach before you start coding?", out _), "questions about a coding task are not a wrap-up");
        PromptBuilder.ClearHistory();
        Check(PromptBuilder.DetectType("Any questions on the approach before you start coding?") != PromptBuilder.QuestionType.CandidateQuestions, "a coding-task check is not an invitation to ask about the role");

        PromptBuilder.ClearHistory();
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "interview turns: all passed" : $"interview turns: {failed} FAILED");
        return failed;
    }
}
