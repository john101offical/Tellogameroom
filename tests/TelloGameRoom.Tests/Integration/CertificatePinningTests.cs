using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TelloGameRoom.Core.Client;

namespace TelloGameRoom.Tests.Integration;

/// <summary>
/// The apps connect to a server without a domain name by pinning its certificate's SHA-256 fingerprint.
/// These tests run a real HTTPS server with the same kind of certificate deploy/caddy/make-cert.sh creates.
/// </summary>
public sealed class CertificatePinningTests
{
    private static X509Certificate2 SelfSigned(string subject, X509Certificate2? issuer = null, bool isCa = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        if (!isCa)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        }

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);
        using var cert = issuer is null
            ? request.CreateSelfSigned(notBefore, notAfter)
            : request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(8)).CopyWithPrivateKey(key);

        // Round-trip through PFX so the private key is usable by SslStream on every OS.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private static async Task<(WebApplication App, Uri Url)> StartHttpsAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address));
    }

    private static async Task<HttpStatusCode> GetAsync(Uri url, params string[] pins)
    {
        using var http = new HttpClient(TelloHttp.CreateHandler(pins)) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(10) };
        return (await http.GetAsync("health")).StatusCode;
    }

    [Fact]
    public async Task Pinned_self_signed_certificate_is_accepted_and_anything_else_is_refused()
    {
        using var cert = SelfSigned("TelloGameRoom 127.0.0.1");
        var (app, url) = await StartHttpsAsync(cert);
        await using var _ = app;
        var fingerprint = TelloHttp.Thumbprint(cert);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(url, fingerprint));

        // The same fingerprint as openssl prints it (with colons, lower case) works too.
        var withColons = string.Join(":", Enumerable.Range(0, 32).Select(i => fingerprint.Substring(i * 2, 2))).ToLowerInvariant();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(url, withColons));

        // No pin, or a wrong pin: the connection is refused.
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(url));
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(url, new string('A', 64)));
    }

    [Fact]
    public async Task Pinning_a_root_the_server_never_sends_does_not_work()
    {
        // This was the old IP-only setup: Caddy's internal authority signed the server certificate, the
        // fingerprint printed was the authority's, and the authority is not part of what the server sends.
        using var root = SelfSigned("Local authority", isCa: true);
        using var leaf = SelfSigned("TelloGameRoom 127.0.0.1", issuer: root);
        var (app, url) = await StartHttpsAsync(leaf);
        await using var _ = app;

        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(url, TelloHttp.Thumbprint(root)));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(url, TelloHttp.Thumbprint(leaf)));
    }
}
