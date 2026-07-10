using System.Net;
using Slopworks.Core.Config;

namespace Slopworks.Core.Http;

/// <summary>
/// One HttpClient for everything (resolver, checksums, downloads). Honors the configured
/// proxy — corporate TLS-intercepting proxies are a first-class scenario. No per-client
/// timeout: large downloads rely on cancellation tokens instead.
/// </summary>
public static class SlopworksHttpClient
{
    public static HttpClient Create(NetworkConfig network)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };

        if (network.Proxy is { } proxy)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = network.AllowSystemProxy;
        }

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Slopworks/0.1");
        return client;
    }
}
