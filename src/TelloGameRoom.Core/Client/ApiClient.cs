using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Core.Client;

/// <summary>A failed API call, carrying the server's stable error code so the UI can show a localized message.</summary>
public sealed class ApiException(HttpStatusCode status, string code, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(detail)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>One of <see cref="ErrorCodes"/>, or <c>network</c> when the server could not be reached.</summary>
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public const string NetworkCode = "network";

    public bool IsNetwork => Code == NetworkCode;
}

/// <summary>Builds HTTP handlers and normalizes server addresses for the Windows clients.</summary>
public static class TelloHttp
{
    /// <summary>Accepts "203.0.113.5", "shop.example.com:8443" or a full URL and returns an absolute base URI. HTTPS unless http:// is written out.</summary>
    public static Uri? NormalizeServerUrl(string? input)
    {
        var text = input?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text + "/", UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Length > 0 ? uri : null;
    }

    /// <summary>
    /// Creates a handler that trusts normally valid certificates and, in addition, any chain that contains a certificate
    /// whose SHA-256 thumbprint is pinned. Pinning is how a server without a domain name (self-signed or private CA)
    /// is used safely.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IReadOnlyCollection<string>? pinnedSha256Thumbprints)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (CreateValidationCallback(pinnedSha256Thumbprints) is { } callback)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = callback;
        }

        return handler;
    }

    /// <summary>The certificate check used for both HTTP and WebSocket connections; null when nothing is pinned.</summary>
    public static RemoteCertificateValidationCallback? CreateValidationCallback(IReadOnlyCollection<string>? pinnedSha256Thumbprints)
    {
        var pins = (pinnedSha256Thumbprints ?? []).Select(NormalizeThumbprint).Where(p => p.Length == 64).ToHashSet(StringComparer.Ordinal);
        return pins.Count == 0 ? null : (_, certificate, chain, errors) => errors == SslPolicyErrors.None || IsPinned(certificate, chain, pins);
    }

    public static string NormalizeThumbprint(string value) =>
        new string([.. value.Where(Uri.IsHexDigit)]).ToUpperInvariant();

    public static string Thumbprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static bool IsPinned(X509Certificate? certificate, X509Chain? chain, HashSet<string> pins)
    {
        if (certificate is not null)
        {
            using var leaf = new X509Certificate2(certificate);
            if (pins.Contains(Thumbprint(leaf)))
            {
                return true;
            }
        }

        return chain is not null && chain.ChainElements.Any(e => pins.Contains(Thumbprint(e.Certificate)));
    }
}

/// <summary>
/// Thin typed wrapper over <see cref="HttpClient"/> for the TelloGameRoom REST API: adds the protocol header and
/// authorization, and turns problem responses into <see cref="ApiException"/>.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>Returns the value of the Authorization header (e.g. "Bearer x"), or null for anonymous calls.</summary>
    public Func<CancellationToken, Task<string?>>? AuthorizationProvider { get; set; }

    /// <summary>Called on a 401. Return true after obtaining new credentials to have the request retried once.</summary>
    public Func<CancellationToken, Task<bool>>? UnauthorizedHandler { get; set; }

    public Uri? BaseAddress
    {
        get => http.BaseAddress;
        set => http.BaseAddress = value;
    }

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    /// <summary>GET that maps 204 No Content to null.</summary>
    public async Task<T?> GetOrDefaultAsync<T>(string path, CancellationToken ct = default)
        where T : class
    {
        using var response = await SendCoreAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<T>(TelloJson.Options, ct).ConfigureAwait(false);
    }

    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, Json(body), ct);

    public async Task PostAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Post, path, () => Json(body)!(), ct).ConfigureAwait(false);
    }

    public Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, path, Json(body), ct);

    public async Task PutAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Put, path, () => Json(body)!(), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Delete, path, null, ct).ConfigureAwait(false);
    }

    public Task<T> DeleteAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Delete, path, null, ct);

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>POST with a JSON body that returns a file (e.g. a screenshot).</summary>
    public async Task<byte[]> PostForBytesAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Post, path, () => Json(body)!(), ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends raw bytes (logo, screenshot, backup).</summary>
    public async Task<T?> SendBytesAsync<T>(HttpMethod method, string path, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(method, path, () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }, ct).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0
            ? default
            : await response.Content.ReadFromJsonAsync<T>(TelloJson.Options, ct).ConfigureAwait(false);
    }

    private static Func<HttpContent>? Json(object? body) => () => JsonContent.Create(body ?? new { }, options: TelloJson.Options);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, content, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(TelloJson.Options, ct).ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, ProtocolInfo.ApiPrefix.TrimStart('/') + path) { Content = content?.Invoke() };
            request.Headers.TryAddWithoutValidation(ProtocolInfo.VersionHeader, ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (AuthorizationProvider is not null && await AuthorizationProvider(ct).ConfigureAwait(false) is { } authorization)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ApiException(0, ApiException.NetworkCode, ex.Message);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && UnauthorizedHandler is not null
                && await UnauthorizedHandler(ct).ConfigureAwait(false))
            {
                response.Dispose();
                continue;
            }

            using (response)
            {
                throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ErrorCodes.InvalidCredentials,
            HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
            HttpStatusCode.NotFound => ErrorCodes.NotFound,
            _ => "http_" + (int)response.StatusCode,
        };
        var detail = response.ReasonPhrase ?? code;
        Dictionary<string, string[]>? errors = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    code = c.GetString()!;
                }

                if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    detail = d.GetString()!;
                }

                if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    errors = e.EnumerateObject().ToDictionary(p => p.Name,
                        p => p.Value.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray());
                    var first = errors.Values.SelectMany(x => x).FirstOrDefault();
                    if (first is not null && !root.TryGetProperty("detail", out _))
                    {
                        detail = first;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not a problem document (e.g. an HTML error page from a proxy); keep the status-based defaults.
        }

        return new ApiException(response.StatusCode, code, detail, errors);
    }
}
