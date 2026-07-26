using System.Text.Json;

namespace LlamaTray;

internal sealed class AppConfig
{
    public int Port { get; set; } = 8080;
    public string Root { get; set; } = @"D:\llama.cpp";
    public string ServerExe { get; set; } = @"D:\llama.cpp\src\build\bin\llama-server.exe";
    public string ModelsDir { get; set; } = @"D:\llama.cpp\models";
    public string PresetIni { get; set; } = @"D:\llama.cpp\models.ini";
    public string StdOutLog { get; set; } = @"D:\llama.cpp\server.out.log";
    public string StdErrLog { get; set; } = @"D:\llama.cpp\server.err.log";
    public int MaxModels { get; set; } = 1;
    public int AutoUnloadMinutes { get; set; } = 15;

    public string BaseUrl => $"http://127.0.0.1:{Port}";
}

/// <summary>Loads settings from appsettings.json + appsettings.local.json (layered) next to the exe.</summary>
internal static class ServerConfig
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static AppConfig Current { get; private set; } = Load();

    internal static AppConfig Load()
    {
        var cfg = new AppConfig();

        cfg = ApplyLayer(cfg, Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        cfg = ApplyLayer(cfg, Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"));

        return cfg;
    }

    private static AppConfig ApplyLayer(AppConfig baseCfg, string path)
    {
        if (!File.Exists(path))
            return baseCfg;

        try
        {
            var json = File.ReadAllText(path);
            var layer = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (layer == null)
                return baseCfg;

            var defaults = new AppConfig();
            return new AppConfig
            {
                Port = layer.Port != defaults.Port ? layer.Port : baseCfg.Port,
                Root = layer.Root != defaults.Root ? layer.Root : baseCfg.Root,
                ServerExe = layer.ServerExe != defaults.ServerExe ? layer.ServerExe : baseCfg.ServerExe,
                ModelsDir = layer.ModelsDir != defaults.ModelsDir ? layer.ModelsDir : baseCfg.ModelsDir,
                PresetIni = layer.PresetIni != defaults.PresetIni ? layer.PresetIni : baseCfg.PresetIni,
                StdOutLog = layer.StdOutLog != defaults.StdOutLog ? layer.StdOutLog : baseCfg.StdOutLog,
                StdErrLog = layer.StdErrLog != defaults.StdErrLog ? layer.StdErrLog : baseCfg.StdErrLog,
                MaxModels = layer.MaxModels != defaults.MaxModels ? layer.MaxModels : baseCfg.MaxModels,
                AutoUnloadMinutes = layer.AutoUnloadMinutes != defaults.AutoUnloadMinutes
                    ? layer.AutoUnloadMinutes
                    : baseCfg.AutoUnloadMinutes,
            };
        }
        catch
        {
            return baseCfg;
        }
    }

    public static void SaveOverrides(AppConfig config)
    {
        var defaults = new AppConfig();
        var overrides = new Dictionary<string, object>();

        if (config.Port != defaults.Port)
            overrides["Port"] = config.Port;
        if (config.ServerExe != defaults.ServerExe)
            overrides["ServerExe"] = config.ServerExe;
        if (config.ModelsDir != defaults.ModelsDir)
            overrides["ModelsDir"] = config.ModelsDir;
        if (config.PresetIni != defaults.PresetIni)
            overrides["PresetIni"] = config.PresetIni;
        if (config.StdOutLog != defaults.StdOutLog)
            overrides["StdOutLog"] = config.StdOutLog;
        if (config.StdErrLog != defaults.StdErrLog)
            overrides["StdErrLog"] = config.StdErrLog;
        if (config.MaxModels != defaults.MaxModels)
            overrides["MaxModels"] = config.MaxModels;
        if (config.AutoUnloadMinutes != defaults.AutoUnloadMinutes)
            overrides["AutoUnloadMinutes"] = config.AutoUnloadMinutes;

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");

        if (overrides.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        else
        {
            var json = JsonSerializer.Serialize(overrides, JsonOptions);
            File.WriteAllText(path, json);
        }

        Current = config;
    }

    public static void Reload()
    {
        Current = Load();
    }
}
