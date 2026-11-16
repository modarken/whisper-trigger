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

    // Instant local check: is anything listening on TypeWhisper's port? A refused connect
    // to localhost takes ~2 s on Windows (SYN retries), so a wake-up polls this first
    // instead of paying that on every IsReachable attempt while TypeWhisper starts.
    public bool IsListening()
    {
        try
        {
            int port = new Uri(Discover().url).Port;
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return true; } // can't tell → let IsReachable decide
    }

    // Start/stop get a long timeout: after a long idle TypeWhisper can take well over 5 s
    // to answer the first start (it respawns its transcription worker), and a stop may
    // wait on transcription. The old 5 s limit made those look like failures — while
    // TypeWhisper went on to start recording anyway. The UI shows STARTING meanwhile.
    private static readonly TimeSpan DictationTimeout = TimeSpan.FromSeconds(20);

    public bool Start() => Post("/v1/dictation/start", DictationTimeout);
    public bool Stop()  => Post("/v1/dictation/stop", DictationTimeout);

    // Loading a model spawns TypeWhisper's transcription worker and reads the model from
    // disk (~3 s measured, longer on a cold disk), so it gets its own, longer timeout.
    private readonly HttpClient _slowHttp = new() { Timeout = TimeSpan.FromSeconds(90) };

    public sealed record ModelInfo(string Engine, string Model, bool Loaded);

    // The active model and whether it's loaded, or null if unreachable/unknown.
    public ModelInfo? GetActiveModel()
    {
        var (url, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Get, url + "/v1/models");
        if (token is not null) AddAuthHeader(req, token);
        try
        {
            using var resp = _http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = doc.RootElement;
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var m in models.EnumerateArray())
            {
                if (!(m.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True)) continue;
                string? engine = m.TryGetProperty("engine", out var e) ? e.GetString() : null;
                string? id     = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                bool loaded    = m.TryGetProperty("loaded", out var l) && l.ValueKind == JsonValueKind.True;
                if (engine is not null && id is not null) return new ModelInfo(engine, id, loaded);
            }
        }
        catch { }
        return null;
    }

    // The engine status from /v1/status ("ready", "no_model", …), or null if unreachable.
    // After /v1/models/unload TypeWhisper reports "no_model" and no model is active at all.
    public string? GetEngineStatus(TimeSpan? timeout = null)
    {
        var (url, _) = Discover();
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            using var resp = _http.Send(new HttpRequestMessage(HttpMethod.Get, url + "/v1/status"), cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        catch { return null; }
    }

    // The user's chosen model as TypeWhisper persists it, for reloading after an unload
    // we didn't do ourselves (or don't remember — e.g. Whisper Trigger restarted since).
    // settings.json stores "selectedModelId": "plugin:<plugin-id>:<model>"; the engine
    // comes from /v1/models, which keeps reporting it even with no model loaded.
    public ModelInfo? GetSelectedModel()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string? path = DiscoveryDirs
                .Select(d => Path.Combine(localAppData, d, "settings.json"))
                .FirstOrDefault(File.Exists);
            if (path is null) return null;
            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            if (!settings.RootElement.TryGetProperty("selectedModelId", out var sel) ||
                sel.GetString() is not { Length: > 0 } selected) return null;
            string model = selected[(selected.LastIndexOf(':') + 1)..];

            var (url, token) = Discover();
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/v1/models");
            if (token is not null) AddAuthHeader(req, token);
            using var resp = _http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            // Prefer the engine listed for that model; fall back to the top-level engine.
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() == model &&
                        m.TryGetProperty("engine", out var e) && e.GetString() is { } eng)
                        return new ModelInfo(eng, model, false);
            return doc.RootElement.TryGetProperty("engine", out var top) && top.GetString() is { } engine
                ? new ModelInfo(engine, model, false) : null;
        }
        catch { return null; }
    }

    public bool LoadModel(ModelInfo m)   => PostModel("/v1/models/load", m, _slowHttp);
    public bool UnloadModel(ModelInfo m) => PostModel("/v1/models/unload", m, _http);

    private bool PostModel(string path, ModelInfo m, HttpClient http)
    {
        var (url, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Post, url + path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { engine = m.Engine, model = m.Model }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (token is not null) AddAuthHeader(req, token);
        try
        {
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode)
                Log.Write($"{path} -> HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Write($"{path} failed: {ex.Message}");
            return false;
        }
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

    private bool Post(string path, TimeSpan timeout)
    {
        var (url, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Post, url + path);
        if (token is not null) AddAuthHeader(req, token);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var resp = _slowHttp.Send(req, cts.Token);
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

    // TypeWhisper writes the discovery file under TypeWhisper-UserData; older builds used TypeWhisper.
    private static readonly string[] DiscoveryDirs = { "TypeWhisper-UserData", "TypeWhisper" };

    // Re-reads the discovery file on every call so changes take effect without restarting.
    private (string url, string? token) Discover()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string? path = DiscoveryDirs
                .Select(d => Path.Combine(localAppData, d, "api-discovery.json"))
                .FirstOrDefault(File.Exists);
            if (path != null)
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

    public void Dispose()
    {
        _http.Dispose();
        _slowHttp.Dispose();
    }
}
