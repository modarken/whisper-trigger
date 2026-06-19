using System.Net.Http;
using System.Text.Json;

namespace WhisperTrigger;

// Thin wrapper around TypeWhisper's local HTTP API.
// All methods are synchronous — call from a background thread only.
// See ../../TYPEWHISPER-API.md for the full API contract.
sealed class TypeWhisperClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // include auth token — omitting it would always return false when a token is configured
    public bool IsReachable()
    {
        var (url, token) = Discover();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/v1/status");
            if (token is not null) AddAuthHeader(req, token);
            return _http.Send(req).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public bool Start() => Post("/v1/dictation/start");
    public bool Stop()  => Post("/v1/dictation/stop");

    // Polls until TypeWhisper reports idle (transcription + paste complete), then
    // waits an extra buffer so the Ctrl+V paste has time to land before we restore.
    public void WaitForIdle(int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (QueryRecording() == false) break;
            Thread.Sleep(150);
        }
        Thread.Sleep(500); // buffer for TypeWhisper to finish the actual paste
    }

    // Returns null if the status endpoint is unreachable or the response shape is unknown.
    public bool? QueryRecording()
    {
        var (url, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Get, url + "/v1/dictation/status");
        if (token is not null) AddAuthHeader(req, token);
        try
        {
            var resp = _http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            return InterpretRecording(doc.RootElement);
        }
        catch { return null; }
    }

    private bool Post(string path)
    {
        var (url, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Post, url + path);
        if (token is not null) AddAuthHeader(req, token);
        try
        {
            var resp = _http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"TypeWhisper API unreachable ({path}): {ex.Message}\n" +
                "Is the API server enabled? Settings > Advanced > API Server", ex);
        }
    }

    // strip CR/LF to prevent header injection from a tampered api-discovery.json
    private static void AddAuthHeader(HttpRequestMessage req, string token)
    {
        string safe = token.Replace("\r", "").Replace("\n", "");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + safe);
    }

    // Lenient parser — the exact status JSON shape isn't documented; probe common field names.
    private static bool? InterpretRecording(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            string name = prop.Name.ToLowerInvariant();
            if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                (name.Contains("record") || name is "active" or "dictating"))
                return prop.Value.GetBoolean();
            if (prop.Value.ValueKind == JsonValueKind.String && name is "state" or "status")
            {
                string v = prop.Value.GetString()!.ToLowerInvariant();
                if (v is "recording" or "active" or "dictating" or "listening") return true;
                if (v is "idle" or "stopped" or "ready" or "inactive") return false;
            }
        }
        return null;
    }

    // Re-reads the discovery file on every call so changes take effect without restarting.
    private (string url, string? token) Discover()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeWhisper", "api-discovery.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                // api-discovery.json is untrusted — reject out-of-range ports
                int port = 8978;
                if (root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number)
                {
                    int parsed = p.GetInt32();
                    if (parsed is >= 1 and <= 65535) port = parsed;
                }

                string? token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                return ($"http://127.0.0.1:{port}", token);
            }
        }
        catch { }
        return ("http://127.0.0.1:8978", null);
    }

    public void Dispose() => _http.Dispose();
}
