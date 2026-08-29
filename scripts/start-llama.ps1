# Start llama.cpp router server (CUDA) — OpenAI-compatible API on http://127.0.0.1:8080/v1
# Model switching: set the "model" field per request (ids come from GET /v1/models).
param(
    [int]$Port = 8080,
    [int]$MaxModels = 1,          # keep 1: only one model in VRAM at a time (32 GB is tight)
    [int]$Ctx = 0,                # 0 = use per-model ctx-size from models.ini; >0 overrides ALL models
    [string]$LogFile = 'D:\llama.cpp\server.err.log'
)

$ErrorActionPreference = 'Stop'
$bin       = 'D:\llama.cpp\src\build\bin\llama-server.exe'
$modelsDir = 'D:\llama.cpp\models'
$preset    = 'D:\llama.cpp\models.ini'   # per-model overrides (ngl, flash-attn, ctx, kv quant, ...)

# refuse to double-start
$existing = (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique
if ($existing) {
    "Already listening on $Port (PID $existing). Run stop-llama.ps1 first."
    return
}

if (-not (Test-Path -LiteralPath $bin)) { throw "llama-server executable not found: $bin" }

# NOTE: -ngl / -fa intentionally NOT passed here — they live in models.ini [*] so
# they stay per-model overridable (a CLI arg would override every preset value).
$argList = @('--models-dir', $modelsDir, '--models-max', $MaxModels,
             '--parallel', 2, '--kv-unified',
             '--port', $Port, '--host', '127.0.0.1')
if (Test-Path $preset) { $argList += @('--models-preset', $preset) }
if ($Ctx -gt 0)        { $argList += @('-c', $Ctx) }   # optional global ctx override

# The server is launched with no inherited console/pipe handles. Its logging is
# directed by llama-server itself because a shell redirection would reintroduce
# the process-tree handle that makes windows_shell_exec wait for the child.
if ([string]::IsNullOrWhiteSpace($LogFile)) { throw 'LogFile cannot be empty' }
try {
    [IO.File]::WriteAllText($LogFile, '')
}
catch {
    throw "Cannot open log file '$LogFile'. Another server may be using it; use -LogFile for a separate instance. Details: $($_.Exception.Message)"
}
$argList += @('--log-file', $LogFile, '--log-colors', 'off')

# Compile the small native launcher in memory. Start-Process and cmd /c start
# detach the console/window but leave the child in the shell harness's Windows
# job. CREATE_BREAKAWAY_FROM_JOB is the important distinction here.
if (-not ([System.Management.Automation.PSTypeName]'LlamaDetached.NativeLauncher').Type) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LlamaDetached {
    public static class NativeLauncher {
        private const uint DetachedProcess = 0x00000008;
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateNoWindow = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static string BuildCommandLine(string application, string[] args) {
            var command = new StringBuilder(Quote(application));
            if (args != null) {
                foreach (var arg in args) command.Append(' ').Append(Quote(arg ?? String.Empty));
            }
            return command.ToString();
        }

        public static int Launch(string application, string[] args, string currentDirectory) {
            var commandLine = new StringBuilder(BuildCommandLine(application, args));
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
            ProcessInformation processInfo;
            var flags = DetachedProcess | CreateNewProcessGroup | CreateBreakawayFromJob | CreateNoWindow;
            var ok = CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                                     flags, IntPtr.Zero, currentDirectory, ref startup, out processInfo);
            if (!ok) {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "CreateProcessW failed");
            }
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            return processInfo.dwProcessId;
        }

        private static string Quote(string value) {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            var result = new StringBuilder();
            var slashes = 0;
            result.Append('"');
            foreach (var c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') {
                    result.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}
'@
}

$serverPid = $null
$commandLine = [LlamaDetached.NativeLauncher]::BuildCommandLine($bin, [string[]]$argList)
try {
    $serverPid = [LlamaDetached.NativeLauncher]::Launch($bin, [string[]]$argList, (Split-Path -Parent $bin))
}
catch {
    # A restrictive job may reject CREATE_BREAKAWAY_FROM_JOB. WMI asks the
    # Windows management service to create the process outside that job instead.
    $nativeError = $_.Exception.Message
    try {
        $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create `
            -Arguments @{ CommandLine = $commandLine; CurrentDirectory = (Split-Path -Parent $bin) }
        if ($wmi.ReturnValue -ne 0 -or -not $wmi.ProcessId) {
            throw "Win32_Process.Create returned $($wmi.ReturnValue)"
        }
        $serverPid = [int]$wmi.ProcessId
        "Native breakaway was unavailable; launched through WMI process broker."
    }
    catch {
        throw "Detached launch failed. Native: $nativeError; WMI fallback: $($_.Exception.Message)"
    }
}

"llama-server launch requested: PID=$serverPid, URL=http://127.0.0.1:$Port/v1, models-max=$MaxModels"

# Starting is non-blocking: shell callers do not wait on a long-lived descendant.
# Callers that need readiness (load.ps1, probe-ctx-headroom.ps1) poll /health
# themselves at their own cadence rather than this script blocking for them.
