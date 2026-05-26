using System;
using System.Net.Http;

namespace InterviewCopilot
{
    /// <summary>
    /// Single shared HttpClient for the whole application.
    /// HttpClient is designed to be reused — creating one per call exhausts
    /// socket resources under load. All HTTP calls in the app route through here.
    /// </summary>
    internal static class SharedHttpClient
    {
        // One instance, one connection pool, shared across all callers.
        public static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        // Shorter timeout for lightweight calls (credits, token refresh).
        public static readonly HttpClient HttpShort = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
