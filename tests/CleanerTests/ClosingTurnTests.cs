using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// An interviewer can invite one candidate question, answer it, and then ask
/// whether anything else is needed. That second prompt is a social closing, not
/// permission to generate another stack-heavy question. A final thank-you is a
/// sign-off as well, not a request to summarize the interview.
/// </summary>
internal static class ClosingTurnTests
{
    internal static int Run()
    {
        int failed = 0;

        void Check(bool ok, string label, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok)
            {
                failed++;
                if (detail != null) Console.WriteLine($"        {detail}");
            }
        }

        PromptBuilder.ClearHistory();

        string first = "Before we wrap up, is there anything you'd like to ask me about the role or the team?";
        Check(PromptBuilder.IsCandidateQuestionInvitation(first),
            "first invitation is recognized");
        Check(PromptBuilder.IsCandidateQuestionInvitation(
                "Is there any question do you have for me?"),
            "speech-recognized invitation wording is recognized");
        Check(!PromptBuilder.TryGetClosingResponse(first, out _),
            "first invitation still gets one role-aware question");

        PromptBuilder.AddToHistory(first,
            "What would success look like in the first six months?");

        string followUp = "Is that the level of detail you were looking for, or do you want me to go a bit deeper?";
        Check(PromptBuilder.IsCandidateQuestionInvitation(followUp),
            "detail check after an answer is recognized as closing conversation");
        Check(PromptBuilder.TryGetClosingResponse(followUp, out string response),
            "repeat invitation is answered locally");
        Check(!response.Contains('?') && response.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 25,
            "repeat response is short and asks nothing new", response);

        PromptBuilder.AddToHistory(followUp, response);
        string repeated = "What else would you like to know about the role or the team?";
        Check(PromptBuilder.TryGetClosingResponse(repeated, out string repeatedResponse),
            "another repeated invitation cannot restart the loop");
        Check(!repeatedResponse.Contains('?') && !repeatedResponse.Contains("MORE TO SAY"),
            "later closing response stays conversational", repeatedResponse);

        string final = "Thank you, Pavan, for taking the time to discuss the AI research engineer role with us.";
        Check(PromptBuilder.IsInterviewEndStatement(final),
            "final interviewer thank-you is a sign-off");
        Check(PromptBuilder.TryGetClosingResponse(final, out string finalResponse),
            "sign-off gets a local candidate response");
        Check(!finalResponse.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase) &&
              !finalResponse.Contains("MORE TO SAY", StringComparison.OrdinalIgnoreCase),
            "sign-off cannot turn into a technical recap", finalResponse);

        Check(!PromptBuilder.IsCandidateQuestionInvitation(
                "How do you decide which questions to ask users during research?"),
            "ordinary question about asking users is not a closing invitation");
        Check(!PromptBuilder.IsInterviewEndStatement(
                "Thank you. Can you explain how the training pipeline works?"),
            "a polite technical question is not mistaken for the end");

        // ── Real questions that the first version answered with a fixed line ──
        // History still holds an earlier candidate-question invitation here, so
        // each of these would have been answered locally before the fix.
        string opening = "Thank you for taking the time to speak with us today. " +
                         "Can you start by telling me about yourself?";
        Check(!PromptBuilder.IsInterviewEndStatement(opening),
            "opening thank-you with a question is not the end");
        Check(!PromptBuilder.TryGetClosingResponse(opening, out _),
            "opening thank-you goes to the model, not a goodbye");
        Check(!PromptBuilder.IsInterviewEndStatement(
                "Hi Pavan, thanks for joining us today and for your time. Let's begin with your background."),
            "welcome thank-you that moves on is not the end");
        Check(!PromptBuilder.IsInterviewEndStatement(
                "Thanks for your time on that one, now let's move on to the coding round."),
            "mid-interview transition is not the end");

        string angle = "What other angle would you take to reduce the latency here?";
        Check(!PromptBuilder.IsCandidateQuestionInvitation(angle),
            "technical 'other angle' question is not a closing turn");
        Check(!PromptBuilder.TryGetClosingResponse(angle, out _),
            "technical 'other angle' question goes to the model");

        string followOn = "Does that answer your question about how we deploy? " +
                          "So how would you test this service?";
        Check(!PromptBuilder.TryGetClosingResponse(followOn, out _),
            "a new question after 'does that answer' goes to the model");

        string[] spokenRequests =
        {
            "So for this next one I want you to describe how you would design a URL shortener that handles millions of requests",
            "I'd like you to walk me through how you would debug a memory leak in a production Java service running on Kubernetes",
            "Okay now let's say your API latency suddenly doubles after a deploy and you need to find out what changed and fix it",
            "Now imagine you are leading the migration from a monolith to microservices and explain the steps you would take first",
        };
        foreach (string request in spokenRequests)
            Check(PromptBuilder.DetectType(request) != PromptBuilder.QuestionType.ContextStatement,
                "spoken request is answered, not just acknowledged", request);

        Check(PromptBuilder.DetectType(
                "Our team builds the pricing data platform and we mostly work in Scala and Spark " +
                "with a strong focus on reliability and a weekly on call rotation")
              == PromptBuilder.QuestionType.ContextStatement,
            "a genuine explanation of the team is still only acknowledged");

        // ── Second pass: confirmed on the 1.0.20 build before fixing ──────────
        Check(PromptBuilder.IsCandidateQuestionInvitation(
                "Is there another angle on the role, the tech, or the team that you'd like me to focus on?"),
            "another angle on the role or team is an invitation");

        string touchThenQuestion = "We'll be in touch with next steps, but first can you explain your testing approach?";
        Check(!PromptBuilder.IsInterviewEndStatement(touchThenQuestion),
            "'we'll be in touch, but first' is not the end");
        Check(!PromptBuilder.TryGetClosingResponse(touchThenQuestion, out _),
            "'we'll be in touch, but first' goes to the model");

        string unpunctuated = "Does that answer your question so how would you test this service";
        Check(!PromptBuilder.TryGetClosingResponse(unpunctuated, out _),
            "unpunctuated question after 'does that answer' goes to the model");

        string recapThenThanks = "One question about research versus production and one about how the team " +
                                 "works, both good questions. Thank you for taking the time to speak with us today.";
        Check(PromptBuilder.IsInterviewEndStatement(recapThenThanks),
            "recap of earlier questions then a thank-you is still the end");

        Check(PromptBuilder.IsInterviewEndStatement("We'll be in touch."),
            "a plain 'we'll be in touch' is the end");
        Check(PromptBuilder.IsInterviewEndStatement(
                "Thank you for your time today, we'll share next steps by email."),
            "thanks with next steps by email is the end");
        Check(!PromptBuilder.IsInterviewEndStatement("Thanks for your time, any final thoughts"),
            "thanks followed by 'any final thoughts' is answered");

        string[] codingTasks =
        {
            "For this next exercise I want a function that returns the first non-repeating character in a string using Python",
            "For the next task please create a REST API that supports pagination filtering and sorting for a list of products",
            "The next exercise is a SQL query returning the top three customers by total order value in the last year",
            "I'm going to give you a coding exercise now, write a function that reverses a linked list in place",
        };
        foreach (string task in codingTasks)
            Check(PromptBuilder.DetectType(task) == PromptBuilder.QuestionType.Coding,
                "coding task gets code, not an acknowledgement", task);

        Check(PromptBuilder.DetectType(
                "Can you think of a specific project where you and a researcher disagreed on the approach?")
              == PromptBuilder.QuestionType.Behavioral,
            "'can you think of a specific project' asks for a story, not yes or no");
        Check(PromptBuilder.DetectType("Are you authorized to work in the US?")
              == PromptBuilder.QuestionType.YesNo,
            "a genuine yes/no question stays yes/no");

        PromptBuilder.ClearHistory();
        return failed;
    }
}
