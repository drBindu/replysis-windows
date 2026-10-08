using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Tells our own server which app is calling: "windows" and the version, in two headers on every request to it.
    ///
    /// The admin page uses them to show who has the Windows app, who has the Mac app and who is on the website, and which version.
    /// They go to our server only: a request to GitHub, Google or anyone else carries nothing extra. They are labels, never a
    /// credential, and the server decides nothing from them.
    /// </summary>
    internal sealed class AppIdentityHandler : DelegatingHandler
    {
        private readonly Func<string> _version;
        private readonly Func<string> _backendHost;

        public AppIdentityHandler(HttpMessageHandler inner, Func<string> version, Func<string> backendHost) : base(inner)
        {
            _version = version;
            _backendHost = backendHost;
        }

        /// <summary>Whether this address is our own server. Exact host match, so a look-alike address never gets the headers.</summary>
        internal static bool IsOurServer(Uri? address, string backendHost)
        {
            if (address == null || string.IsNullOrEmpty(backendHost)) return false;
            return string.Equals(address.Host, backendHost, StringComparison.OrdinalIgnoreCase);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (IsOurServer(request.RequestUri, _backendHost()) && !request.Headers.Contains("X-App-Platform"))
                {
                    request.Headers.TryAddWithoutValidation("X-App-Platform", "windows");
                    string version = _version();
                    if (!string.IsNullOrWhiteSpace(version))
                        request.Headers.TryAddWithoutValidation("X-App-Version", version);
                }
            }
            catch
            {
                // A label must never be the reason a request fails.
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
