using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EffectApk.Core;

/// <summary>Сохранённое состояние одного приложения: разрешение Android-дисплея и геометрия окна (DIP).</summary>
public sealed class PackageWindowState
{
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
}

public sealed class AppSettings
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EffectApk");

    private static string FilePath => Path.Combine(RootDir, "settings.json");

    public string? SdkRoot { get; set; }
    public string? ScrcpyDir { get; set; }
    public string AvdName { get; set; } = "EffectApk";

    /// <summary>Фиксированный порт консоли эмулятора → предсказуемый adb-serial «emulator-5580».</summary>
    public int EmulatorPort { get; set; } = 5580;

    /// <summary>Стартовое разрешение виртуального дисплея, на котором scrcpy запускает приложение.</summary>
    public int DisplayWidth { get; set; } = 1080;
    public int DisplayHeight { get; set; } = 1920;
    public int DisplayDpi { get; set; } = 320;

    /// <summary>
    /// -gpu режим эмулятора: host (аппаратный) | swiftshader_indirect (софтверный, совместимый).
    /// ВАЖНО: «auto» вместе с -no-window выбирает SwiftShader, то есть рендерит весь Android
    /// UI на CPU — картинка ощутимо лагает. Поэтому по умолчанию host, с автооткатом.
    /// </summary>
    public string GpuMode { get; set; } = "host";

    public bool SetupCompleted { get; set; }

    /// <summary>Автозагрузка уже настраивалась (по умолчанию включается один раз; дальше решает пользователь через трей).</summary>
    public bool AutoStartConfigured { get; set; }

    /// <summary>Запомненная геометрия окна и разрешение дисплея для каждого пакета (ключ — имя пакета).</summary>
    public Dictionary<string, PackageWindowState> Windows { get; set; } = new();

    [JsonIgnore] public string EmulatorSerial => $"emulator-{EmulatorPort}";
    [JsonIgnore] public string? AdbPath => SdkFile("platform-tools", "adb.exe");
    [JsonIgnore] public string? EmulatorPath => SdkFile("emulator", "emulator.exe");

    [JsonIgnore]
    public string? ScrcpyExe
    {
        get
        {
            if (ScrcpyDir == null) return null;
            var path = Path.Combine(ScrcpyDir, "scrcpy.exe");
            return File.Exists(path) ? path : null;
        }
    }

    [JsonIgnore]
    public string? Aapt2Path
    {
        get
        {
            if (SdkRoot == null) return null;
            var buildTools = Path.Combine(SdkRoot, "build-tools");
            if (!Directory.Exists(buildTools)) return null;
            return Directory.GetDirectories(buildTools)
                .OrderByDescending(Path.GetFileName)
                .Select(dir => Path.Combine(dir, "aapt2.exe"))
                .FirstOrDefault(File.Exists);
        }
    }

    [JsonIgnore]
    public string AvdDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd", AvdName + ".avd");

    private string? SdkFile(params string[] parts)
    {
        if (SdkRoot == null) return null;
        var path = Path.Combine([SdkRoot, .. parts]);
        return File.Exists(path) ? path : null;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // Миграция: «auto» в headless-режиме означал софтверный рендеринг (лаги)
                if (loaded.GpuMode is "auto")
                {
                    loaded.GpuMode = "host";
                    loaded.Save();
                    Logger.Info("Настройки: GPU-режим переключён с auto на host");
                }
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось прочитать settings.json — используются значения по умолчанию", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
