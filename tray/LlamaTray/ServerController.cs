using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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

    // Process we started (if any). Used to short-circuit log-file detection.
    private Process? _serverProcess;
    private string? _logFilePath;

    // Cached detection of an externally-started server's log file.
    private int? _detectedPid;
    private string? _detectedLogFile;
    private DateTime _lastDetectUtc;

    /// <summary>
    /// The log file the current server is actually writing to.  When the tray started
    /// the server this is the temp file it created; when the server was started by a
    /// script or another tool we sniff the --log-file argument from its command line
    /// via WMI.  Falls back to the configured LogFile if no --log-file was passed.
    /// </summary>
    public string ActiveLogFile
    {
        get
        {
            // Fast path: we started the server and it's still running.
            if (_logFilePath != null)
            {
                try { if (_serverProcess is { HasExited: false }) return _logFilePath; }
                catch { /* process handle invalid */ }
            }

            // Slow path: detect from the listening process (throttled to once / 3 s).
            if (DateTime.UtcNow - _lastDetectUtc > TimeSpan.FromSeconds(3))
            {
                _lastDetectUtc = DateTime.UtcNow;
                _detectedPid = GetListeningPid(ServerConfig.Current.Port);
                _detectedLogFile = _detectedPid != null
                    ? DetectLogFileFromProcess(_detectedPid.Value)
                    : null;
            }

            return _detectedLogFile ?? _logFilePath ?? ServerConfig.Current.LogFile;
        }
    }

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

        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"llama-server-tray-{DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture)}.log");

        // Best-effort cleanup of the previous launch's temp log.
        try { if (_logFilePath != null && File.Exists(_logFilePath)) File.Delete(_logFilePath); }
        catch { /* someone still has it open */ }

        _logFilePath = logPath;
        args.Add("--log-file");
        args.Add(logPath);

        var psi = new ProcessStartInfo
        {
            FileName = ServerConfig.Current.ServerExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
            _serverProcess = proc;
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch llama-server.exe: {ex.Message}");
        }

        for (var i = 0; i < 30; i++)
        {
            if (proc.HasExited)
            {
                _serverProcess = null;
                return (false, $"llama-server exited during startup (code {proc.ExitCode}); check {ActiveLogFile}.");
            }
            if (await IsHealthyAsync()) return (true, "llama-server is up.");
            await Task.Delay(700);
        }

        // Never leave a failed startup process behind. It can otherwise make the
        // next action appear stuck or cause StartAsync to report a false conflict.
        try
        {
            if (!proc.HasExited)
                KillTree(proc.Id);
        }
        catch { /* process may have exited between checks */ }
        _serverProcess = null;
        return (false, $"Server did not become healthy in time; check {ActiveLogFile}.");
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
            if (!IsPortListening())
                return (false, $"Server stopped while loading '{modelId}'. Check {ActiveLogFile}.");

            var models = await GetModelsAsync();
            var m = models?.FirstOrDefault(x => x.Id == modelId);
            var status = m?.Status?.Value;
            if (status == "loaded")
                return (true, $"Loaded: {modelId}");

            // Failed loads (notably CUDA OOM) may be reported as an error state
            // instead of returning a failed HTTP response. Do not leave the tray
            // action busy until the full timeout in that case.
            if (status is "error" or "failed" or "unloaded")
                return (false, $"Load failed for '{modelId}'. Check {ActiveLogFile}.");

            await Task.Delay(500);
        }

        return (false, $"Load requested but '{modelId}' was not confirmed loaded after 60s — check {ActiveLogFile}.");
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

    /// <summary>MIB_TCP_STATE_LISTEN — the only state the tray's own server ever sits in.</summary>
    private const int MibTcpStateListen = 2;

    /// <summary>Max IPv4 TCP sessions scanned in a single GetTcpTable() snapshot. If the real table
    /// exceeds this the sizing call signals ERROR_INSUFFICIENT_BUFFER and we fall back to the WMI
    /// command-line match instead.</summary>
    private const int TcpTableRowCapacity = 1024;

    /// <summary>DWORD buffer for MIB_TCPTABLE: 1 header (dwNumEntries) + rows × 7 DWORD fields.</summary>
    private const int TcpTableDwordCapacity = 1 + TcpTableRowCapacity * 7;

    [DllImport("iphlpapi.dll")]
    private static extern int GetTcpTable(byte[] table, ref int size, int order);

    /// <summary>
    /// PID of the process listening on <paramref name="port"/>, or null if none can be determined.
    /// Previously this shelled out to `netstat -ano`. netstat.exe child processes were left
    /// un-reaped (the 5 s timed wait is a poll, not a guaranteed reap) and Windows then complained
    /// about terminating a lingering netstat.exe at every reboot. The listener is now resolved in
    /// process: a GetTcpTable() snapshot, with a WMI command-line match as fallback.
    /// </summary>
    private static int? GetListeningPid(int port)
    {
        var pid = ListeningPidFromTcpTable(port);
        if (pid != null) return pid;
        return ListeningPidFromCommandLine(port);
    }

    /// <summary>Resolve the listener via WinSock's GetTcpTable() (IPv4). Null if it can't be read.</summary>
    private static int? ListeningPidFromTcpTable(int port)
    {
        try
        {
            var buf = new byte[TcpTableDwordCapacity * 4];
            var size = buf.Length;
            // bOrder=0: the kernel's sort order is irrelevant, we scan for the port ourselves.
            // Use a byte[] rather than ref int[]: the native API writes a packed DWORD buffer,
            // and array marshaling with ref can corrupt the managed array reference on failure.
            if (GetTcpTable(buf, ref size, 0) != 0) return null;

            var n = BitConverter.ToInt32(buf, 0); // dwNumEntries
            if (n > TcpTableRowCapacity) n = TcpTableRowCapacity;

            for (var i = 0; i < n; i++)
            {
                var rowStart = 1 + i * 7; // row i starts right after the header DWORD
                var rowOffset = rowStart * sizeof(int);
                if (BitConverter.ToInt32(buf, rowOffset) != MibTcpStateListen) continue;
                // dwLocalPort arrives big-endian in the low 16 bits of the DWORD.
                var raw = BitConverter.ToInt32(buf, rowOffset + 2 * sizeof(int));
                var listenPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                if (listenPort == port)
                {
                    var pid = BitConverter.ToInt32(buf, rowOffset + 6 * sizeof(int)); // dwPid
                    return pid > 0 ? pid : null;
                }
            }
        }
        catch
        {
            // fall through to the WMI fallback
        }
        return null;
    }

    /// <summary>
    /// Fallback when GetTcpTable can't be consulted: find the llama-server whose command line
    /// declares the target --port, reusing the same WMI command-line read as the log-file probe.
    /// Less precise than the socket table (a port might be unstated or defaulted), but it needs no
    /// subprocess and covers the tray's own and router-style launches.
    /// </summary>
    private static int? ListeningPidFromCommandLine(int port)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("llama-server"))
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {p.Id}");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var cmdLine = obj["CommandLine"]?.ToString();
                    if (cmdLine == null) continue;
                    if (cmdLine.Contains($"--port {port}", StringComparison.Ordinal) ||
                        cmdLine.Contains($"--port={port}", StringComparison.Ordinal))
                        return p.Id;
                }
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>
    /// Read the --log-file argument from a running process's command line via WMI.
    /// </summary>
    private static string? DetectLogFileFromProcess(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                var cmdLine = obj["CommandLine"]?.ToString();
                if (cmdLine == null) continue;
                return ExtractLogFileFromCommandLine(cmdLine);
            }
        }
        catch
        {
            // WMI may be unavailable; fall through.
        }
        return null;
    }

    private static string? ExtractLogFileFromCommandLine(string commandLine)
    {
        var idx = commandLine.IndexOf("--log-file", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = commandLine[(idx + "--log-file".Length)..].TrimStart();
        if (after.Length == 0) return null;

        if (after[0] == '"')
        {
            var end = after.IndexOf('"', 1);
            return end > 1 ? after[1..end] : null;
        }

        var space = after.IndexOf(' ');
        return space >= 0 ? after[..space] : after;
    }
}
