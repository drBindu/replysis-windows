using System;
using System.Net.Http;

namespace InterviewCopilot
{
    internal static class SharedHttpClient
    {
        // Direct by default to avoid WPAD delays. Managed networks can opt into
        // the system proxy in Settings; clients are recreated on app restart.
        private static SocketsHttpHandler MakeHandler() => new SocketsHttpHandler
        {
            UseProxy = SettingsWindow.LoadConfig().UseSystemProxy,
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
