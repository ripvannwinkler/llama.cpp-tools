using System.Text.Json;

namespace LlamaTray;

internal sealed class AppConfig
{
    public int Port { get; init; } = 8080;
    public string Root { get; init; } = @"D:\llama.cpp";
    public string ServerExe { get; init; } = @"D:\llama.cpp\src\build\bin\llama-server.exe";
    public string ModelsDir { get; init; } = @"D:\llama.cpp\models";
    public string PresetIni { get; init; } = @"D:\llama.cpp\models.ini";
    public string StdOutLog { get; init; } = @"D:\llama.cpp\server.out.log";
    public string StdErrLog { get; init; } = @"D:\llama.cpp\server.err.log";
    public int MaxModels { get; init; } = 1;

    public string BaseUrl => $"http://127.0.0.1:{Port}";
}

/// <summary>Loads settings from appsettings.json next to the exe (falls back to defaults if missing/invalid).</summary>
internal static class ServerConfig
{
    public static readonly AppConfig Current = Load();

    private static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppConfig();

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
