using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EchoDeck;

/// <summary>
/// Optional cloud transcription through any OpenAI-compatible "audio/transcriptions" service (OpenAI, Groq, a self-hosted
/// server ...). Off by default: when it is used, the song's audio (as a 16 kHz mono WAV) is uploaded to the configured address.
/// </summary>
internal static class CloudTranscriber
{
    private const long MaxUploadBytes = 24L * 1024 * 1024;   // services typically cap uploads at 25 MB
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tempo.CloudKey.v1");

    // ---- the API key lives in settings.json, encrypted with Windows DPAPI for this user only

    public static string ProtectKey(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public static string? UnprotectKey(string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("Cloud key", ex, "could not decrypt the saved key");
            return null;
        }
    }

    public static bool IsConfigured(AppSettings cfg) =>
        !string.IsNullOrWhiteSpace(cfg.CloudEndpoint) && !string.IsNullOrWhiteSpace(cfg.CloudModel) && UnprotectKey(cfg.CloudKeyProtected) != null;

    public static async Task<string> TranscribeAsync(string audioPath, AppSettings cfg, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        var key = UnprotectKey(cfg.CloudKeyProtected);
        if (key == null)
            throw new WhisperException("No cloud API key is saved. Add one in Settings > Lyrics.", setupProblem: true);
        if (!Uri.TryCreate(cfg.CloudEndpoint.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new WhisperException("The cloud address isn't valid. Check it in Settings > Lyrics.", setupProblem: true);
        if (!File.Exists(audioPath)) throw new WhisperException("The song is no longer there.");

        var work = Path.Combine(Path.GetTempPath(), "Tempo-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report((0.05, "Preparing the audio…"));
            var wav = Path.Combine(work, "input.wav");
            try
            {
                await Task.Run(() => WhisperService.ConvertTo16kMono(audioPath, wav), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new WhisperException("Tempo couldn't read this song's audio (" + ex.Message + ").", inner: ex);
            }

            if (new FileInfo(wav).Length > MaxUploadBytes)
                throw new WhisperException("This song is too long to upload (the limit is about 25 MB, roughly 12 minutes of audio).");

            progress?.Report((0.15, "Uploading and transcribing in the cloud…"));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(File.OpenRead(wav));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", "song.wav");
            form.Add(new StringContent(cfg.CloudModel.Trim()), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            var lang = (cfg.WhisperLanguage ?? "auto").Trim();
            if (lang.Length > 0 && !lang.Equals("auto", StringComparison.OrdinalIgnoreCase)) form.Add(new StringContent(lang), "language");

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(url, form, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new WhisperException("Couldn't reach the cloud service (" + ex.Message + "). Check your connection and the address in Settings > Lyrics.", inner: ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string detail = ErrorMessage(body) ?? response.ReasonPhrase ?? "error";
                    bool auth = (int)response.StatusCode is 401 or 403;
                    throw new WhisperException($"The cloud service refused the request ({(int)response.StatusCode}): {detail}" +
                                               (auth ? " Check the API key in Settings > Lyrics." : ""), setupProblem: auth);
                }

                progress?.Report((0.95, "Building the lyrics…"));
                var lrc = ToLrc(body);
                if (lrc.Length == 0) throw new WhisperException("The cloud service didn't hear any words in this song.");
                return lrc;
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp folder */ }
        }
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
        }
        catch { /* not JSON */ }
        return body.Length > 160 ? body.Substring(0, 160) : body;
    }

    /// <summary>verbose_json segments -> timed .lrc lines.</summary>
    private static string ToLrc(string json)
    {
        var sb = new StringBuilder();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segments.EnumerateArray())
            {
                if (!seg.TryGetProperty("text", out var textEl)) continue;
                var text = (textEl.GetString() ?? "").Trim();
                if (text.Length == 0) continue;

                double start = seg.TryGetProperty("start", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
                sb.Append(LyricsParser.FormatTime((long)Math.Round(start * 1000))).Append(text).Append("\r\n");
            }
        }
        else if (doc.RootElement.TryGetProperty("text", out var all))
        {
            // a service that returns plain text only: untimed lyrics
            foreach (var line in (all.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)) sb.Append(line.Trim()).Append("\r\n");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Picks the engine the user chose in Settings > Lyrics.</summary>
internal static class LyricsGenerator
{
    public static bool IsCloud(AppSettings cfg) => string.Equals(cfg.LyricsEngine, "cloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the chosen engine is ready, otherwise what is missing.</summary>
    public static string? NotReadyReason(AppSettings cfg)
    {
        if (IsCloud(cfg))
            return CloudTranscriber.IsConfigured(cfg) ? null : "The cloud service isn't set up: add an API key in Settings > Lyrics.";

        if (!WhisperService.IsReady(cfg))
            return "AI lyrics run on this PC with whisper.cpp, and it isn't set up yet.";

        var problem = WhisperService.CheckModel(WhisperService.Resolve(cfg).Model);
        return problem == null ? null : "Model missing or invalid (" + problem + ").";
    }

    public static Task<string> GenerateAsync(string audioPath, AppSettings cfg, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct) =>
        IsCloud(cfg)
            ? CloudTranscriber.TranscribeAsync(audioPath, cfg, progress, ct)
            : WhisperService.TranscribeAsync(audioPath, cfg, progress, ct);
}
