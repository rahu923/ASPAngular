# A managed callback avoids invoking PowerShell scriptblocks from HttpClient worker threads.
if (!('OrderManagement.TestHttpsHandler' -as [type])) {
    Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
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
