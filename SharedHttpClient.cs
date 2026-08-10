using System;
using System.Net.Http;

namespace InterviewCopilot
{
    internal static class SharedHttpClient
    {
        // UseProxy = false skips Windows WPAD proxy auto-detection, which can hang
        // for 10–30 seconds on networks without a PAC file.
        private static SocketsHttpHandler MakeHandler() => new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        public static readonly HttpClient Http = new HttpClient(MakeHandler())
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        public static readonly HttpClient HttpShort = new HttpClient(MakeHandler())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
