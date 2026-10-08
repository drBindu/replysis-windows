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

        // Our own server is told which app is calling (see AppIdentityHandler). Other addresses get nothing extra.
        private static HttpMessageHandler MakeIdentifiedHandler() => new AppIdentityHandler(
            MakeHandler(),
            () => AppUpdates.CurrentVersion,
            BackendHost);

        private static string? _backendHost;
        private static string BackendHost()
        {
            if (_backendHost != null) return _backendHost;
            try { _backendHost = new Uri(SettingsWindow.GetBackendUrl()).Host; }
            catch { _backendHost = ""; }
            return _backendHost;
        }

        public static readonly HttpClient Http = new HttpClient(MakeIdentifiedHandler())
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        public static readonly HttpClient HttpShort = new HttpClient(MakeIdentifiedHandler())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
