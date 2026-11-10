using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
namespace OrderManagement.Api.Deployment;
public static class ProxyConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Inspect only the immediate proxy hop. An unrestricted forwarded chain can let
        // a caller impersonate an IP/scheme and influence redirects or security decisions.
        options.ForwardLimit = 1;
        // Keep loopback defaults; add only the hosting network ranges explicitly configured by operators.
        foreach (var value in config.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
            options.KnownProxies.Add(IPAddress.Parse(value));
        foreach (var value in config.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? Array.Empty<string>())
        {
            var parts = value.Split('/');
            if (parts.Length != 2) throw new InvalidOperationException("Expected CIDR for a trusted proxy network.");
            var prefix = int.Parse(parts[1]);
            if (prefix <= 0) throw new InvalidOperationException("Trusting every proxy address is forbidden.");
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(parts[0]), prefix));
        }
    }
}
