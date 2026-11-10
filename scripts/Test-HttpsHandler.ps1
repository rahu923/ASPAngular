# A managed callback avoids invoking PowerShell scriptblocks from HttpClient worker threads.
if (!('OrderManagement.TestHttpsHandler' -as [type])) {
    # Resolve the assemblies from the running PowerShell runtime: certificate types
    # moved between assemblies across .NET versions used by hosted agents.
    $httpsReferences = @(
        [System.Net.Http.HttpClientHandler].Assembly.Location
        [System.Security.Cryptography.X509Certificates.X509Certificate2].Assembly.Location
        [System.Net.Security.SslPolicyErrors].Assembly.Location
    ) | Select-Object -Unique
    Add-Type -ReferencedAssemblies $httpsReferences -TypeDefinition @'
using System;
using System.Net.Http;
namespace OrderManagement {
    public static class TestHttpsHandler {
        public static HttpClientHandler Create(string thumbprint) {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                certificate != null && String.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);
            return handler;
        }
    }
}
'@
}
