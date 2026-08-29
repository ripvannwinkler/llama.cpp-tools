using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaTray;

internal sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] ModelStatus? Status);

internal sealed record ModelStatus(
    [property: JsonPropertyName("value")] string Value);

internal sealed record ModelsResponse(
    [property: JsonPropertyName("data")] List<ModelInfo> Data);

internal sealed record SlotInfo(
    [property: JsonPropertyName("is_processing")] bool IsProcessing);

/// <summary>
/// Native equivalent of start-llama.ps1 / stop-llama.ps1 / restart-llama.ps1 / load.ps1 / unload-llama.ps1.
/// </summary>
internal sealed class ServerController
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private Process? _serverProcess;
    private StreamWriter? _stdOutWriter;
    private StreamWriter? _stdErrWriter;

    public bool IsPortListening()
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.Any(l => l.Port == ServerConfig.Current.Port);
    }

    /// <summary>
    /// Total dedicated GPU (VRAM) memory currently committed system-wide, in GiB, summed
    /// across all GPU adapters. Per-process attribution via "GPU Process Memory" proved
    /// unreliable for CUDA compute workloads (llama-server showed 0 even under load), so
    /// this reports the same system-wide figure Task Manager's Performance > GPU tab shows.
    /// Returns null if the usage can't be determined.
    /// </summary>
    public double? GetVramUsageGiB()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Adapter Memory")) return null;

            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            long totalBytes = 0;
            foreach (var instance in category.GetInstanceNames())
            {
                using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, readOnly: true);
                totalBytes += (long)counter.NextValue();
            }

            return totalBytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ModelInfo>?> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{ServerConfig.Current.BaseUrl}/models", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ModelsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Data ?? new List<ModelInfo>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if any slot serving <paramref name="modelId"/> is currently processing a request;
    /// null if the check couldn't be completed (treat as "unknown" — don't count as idle).
    ///
    /// autoload=false is REQUIRED: in router mode a model-scoped request normally auto-loads the
    /// model to serve it (--models-autoload), so probing /slots for an unloaded model would reload
    /// it — this is what caused a just-unloaded model to come back ~1s later. With autoload=false
    /// the router returns "model is not loaded" instead, and this method returns null. Do not remove.
    /// </summary>
    public async Task<bool?> IsModelBusyAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(
                $"{ServerConfig.Current.BaseUrl}/slots?model={Uri.EscapeDataString(modelId)}&autoload=false", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var slots = JsonSerializer.Deserialize<List<SlotInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return slots?.Any(s => s.IsProcessing) ?? false;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cumulative llama_decode() call count for <paramref name="modelId"/>, scraped from its
    /// Prometheus /metrics endpoint (requires "metrics = on" in the model's preset). This is a
    /// monotonic counter, so comparing it across polls catches any decoding that happened between
    /// two polls — unlike /slots' "is_processing", which only reflects the instant the poll landed
    /// and can miss activity shorter than the poll interval. Null if metrics aren't available
    /// (endpoint disabled, model not up, parse failure).
    ///
    /// autoload=false is REQUIRED: in router mode a model-scoped request normally auto-loads the
    /// model to serve it (--models-autoload), so probing /metrics for an unloaded model would reload
    /// it — this is what caused a just-unloaded model to come back ~1s later. With autoload=false
    /// the router returns "model is not loaded" instead, and this method returns null. Do not remove.
    /// </summary>
    public async Task<double?> GetDecodeTotalAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(
                $"{ServerConfig.Current.BaseUrl}/metrics?model={Uri.EscapeDataString(modelId)}&autoload=false", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("llamacpp:n_decode_total ", StringComparison.Ordinal)) continue;
                var value = trimmed["llamacpp:n_decode_total ".Length..].Trim();
                if (double.TryParse(value, out var parsed)) return parsed;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{ServerConfig.Current.BaseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool ok, string message)> StartAsync()
    {
        if (IsPortListening())
            return (false, $"Already listening on {ServerConfig.Current.Port}.");

        var args = new List<string>
        {
            "--models-dir", ServerConfig.Current.ModelsDir,
            "--models-max", ServerConfig.Current.MaxModels.ToString(),
            "--kv-unified",
            "--port", ServerConfig.Current.Port.ToString(),
            "--host", "127.0.0.1",
        };
        if (File.Exists(ServerConfig.Current.PresetIni))
        {
            args.Add("--models-preset");
            args.Add(ServerConfig.Current.PresetIni);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ServerConfig.Current.ServerExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _stdOutWriter = new StreamWriter(ServerConfig.Current.StdOutLog, append: false, Encoding.UTF8) { AutoFlush = true };
        _stdErrWriter = new StreamWriter(ServerConfig.Current.StdErrLog, append: false, Encoding.UTF8) { AutoFlush = true };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) _stdOutWriter?.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _stdErrWriter?.WriteLine(e.Data); };
        proc.Exited += (_, _) =>
        {
            _stdOutWriter?.Dispose();
            _stdErrWriter?.Dispose();
            _stdOutWriter = null;
            _stdErrWriter = null;
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _serverProcess = proc;
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch llama-server.exe: {ex.Message}");
        }

        for (var i = 0; i < 30; i++)
        {
            if (await IsHealthyAsync()) return (true, "llama-server is up.");
            await Task.Delay(700);
        }

        return (false, "Server did not become healthy in time; check server.err.log.");
    }

    public async Task<(bool ok, string message)> StopAsync()
    {
        var pid = GetListeningPid(ServerConfig.Current.Port);
        if (pid.HasValue)
        {
            KillTree(pid.Value);
        }

        for (var pass = 0; pass < 3; pass++)
        {
            var stray = Process.GetProcessesByName("llama-server");
            if (stray.Length == 0) break;
            foreach (var p in stray)
            {
                try { KillTree(p.Id); } catch { /* already gone */ }
            }
            await Task.Delay(600);
        }

        var left = Process.GetProcessesByName("llama-server").Length;
        return left == 0
            ? (true, "Server stopped.")
            : (false, $"WARNING: {left} llama-server process(es) still alive.");
    }

    public async Task<(bool ok, string message)> RestartAsync()
    {
        var (_, stopMsg) = await StopAsync();
        var (ok, startMsg) = await StartAsync();
        return (ok, $"{stopMsg} {startMsg}");
    }

    public async Task<(bool ok, string message)> LoadModelAsync(string modelId)
    {
        if (!IsPortListening())
        {
            var (ok, msg) = await StartAsync();
            if (!ok) return (false, msg);
        }

        try
        {
            var body = JsonSerializer.Serialize(new { model = modelId });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{ServerConfig.Current.BaseUrl}/models/load", content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                if (!err.Contains("already running", StringComparison.OrdinalIgnoreCase))
                    return (false, $"Load failed: {err}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Load request failed: {ex.Message}");
        }

        for (var i = 0; i < 120; i++)
        {
            var models = await GetModelsAsync();
            var m = models?.FirstOrDefault(x => x.Id == modelId);
            if (m?.Status?.Value == "loaded") return (true, $"Loaded: {modelId}");
            await Task.Delay(500);
        }

        return (false, $"Load requested but '{modelId}' not confirmed loaded after 60s — check server.err.log.");
    }

    public async Task<bool> UnloadModelAsync(string modelId)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { model = modelId });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{ServerConfig.Current.BaseUrl}/models/unload", content);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            // ignore — mirrors unload-llama.ps1 swallowing "not running" errors
            return false;
        }
    }

    public async Task<(bool ok, string message)> UnloadAllAsync()
    {
        if (!await IsHealthyAsync())
            return (false, "Server not responding.");

        var models = await GetModelsAsync();
        if (models == null) return (false, "Could not fetch model list.");

        var unloaded = new List<string>();
        foreach (var m in models)
        {
            if (m.Status?.Value != "loaded") continue;
            if (await UnloadModelAsync(m.Id)) unloaded.Add(m.Id);
        }

        await Task.Delay(500);
        return (true, unloaded.Count > 0 ? $"Unloaded: {string.Join(", ", unloaded)}" : "Nothing was loaded.");
    }

    private static void KillTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // process already exited
        }
    }

    private static int? GetListeningPid(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.Ordinal)) continue;
                if (!line.Contains($":{port} ", StringComparison.Ordinal) &&
                    !line.TrimEnd().EndsWith($":{port}", StringComparison.Ordinal)) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var localAddr = parts[1];
                if (!localAddr.EndsWith($":{port}", StringComparison.Ordinal)) continue;

                if (int.TryParse(parts[^1], out var pid)) return pid;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }
}
