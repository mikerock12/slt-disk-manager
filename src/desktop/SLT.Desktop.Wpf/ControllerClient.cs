using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace SLT.Desktop;

public sealed class ControllerClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ControllerClient(string url, string token, string pin)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new InvalidOperationException("O painel administrativo deve conectar por HTTPS a localhost ou endereço de loopback do servidor.");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Informe o token administrativo local do servidor.");
        pin = pin.Trim().Replace(":", "").Replace(" ", "");
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new InvalidOperationException("O pin deve ter 64 caracteres hexadecimais SHA-256.");
        var expected = Convert.FromHexString(pin);
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), expected)
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }
    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object data, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, data, ct);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (data is not null) request.Content = JsonContent.Create(data, options: Json);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("error", out var message)) body = message.ToString();
            }
            catch (JsonException) { body = response.ReasonPhrase ?? "Erro no servidor"; }
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("O servidor respondeu sem conteúdo válido.");
    }
    public static ConnectionSettings ReadConnection(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        string Read(params string[] names)
        {
            foreach (var name in names) if (root.TryGetProperty(name, out var value)) return value.GetString() ?? "";
            return "";
        }
        return new ConnectionSettings(Read("url", "serverUrl", "adminUrl"), Read("token", "adminToken"), Read("serverPin", "pin", "certificatePin"));
    }
    public void Dispose() => _http.Dispose();
}
public sealed record ConnectionSettings(string Url, string Token, string ServerPin);
