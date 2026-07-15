using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace EffectApk.Core;

public sealed record SetupProgress(string Message, double? Fraction = null, bool Detail = false);

/// <summary>
/// Одноразовая настройка Android-рантайма: cmdline-tools → sdkmanager (platform-tools,
/// emulator, build-tools, AOSP system-image) → headless-AVD → scrcpy.
/// Единственное место в приложении, где есть сетевые вызовы (dl.google.com и github.com).
/// </summary>
public sealed class RuntimeBootstrapper(AppSettings settings)
{
    // Закреплённые версии — обновляются вручную при выходе новых релизов.
    private const string CmdlineToolsUrl = "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip";

    /// <summary>Версия scrcpy: её же передаём scrcpy-server при запуске (сервер сверяет строго).</summary>
    public const string ScrcpyVersion = "3.3.1";
    private static string ScrcpyUrl => $"https://github.com/Genymobile/scrcpy/releases/download/v{ScrcpyVersion}/scrcpy-win64-v{ScrcpyVersion}.zip";

    // AOSP-образ: без Google Play и GMS — магазина приложений не будет по построению
    private const string SystemImage = "system-images;android-34;default;x86_64";
    private const string BuildToolsPackage = "build-tools;34.0.0";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Каких компонентов рантайма не хватает (пустой список — всё на месте).</summary>
    public IReadOnlyList<string> Validate()
    {
        var missing = new List<string>();
        if (settings.AdbPath == null) missing.Add("adb (platform-tools)");
        if (settings.EmulatorPath == null) missing.Add("Android Emulator");
        if (settings.Aapt2Path == null) missing.Add("aapt2 (build-tools)");
        if (settings.ScrcpyExe == null) missing.Add("scrcpy");
        if (!Directory.Exists(settings.AvdDir)) missing.Add($"AVD «{settings.AvdName}»");
        return missing;
    }

    /// <summary>Уже установленный SDK (Android Studio и т.п.) — чтобы не качать 2 ГБ зря.</summary>
    public static string? DetectExistingSdk()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        ];
        return candidates.FirstOrDefault(c =>
            c != null && File.Exists(Path.Combine(c, "platform-tools", "adb.exe")));
    }

    public async Task RunAsync(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (!TryFindJava(out var javaHome))
            throw new InvalidOperationException(
                "Не найдена Java 17+ — она нужна инструментам Android SDK (sdkmanager/avdmanager). " +
                "Установите Temurin JDK 17 с https://adoptium.net и запустите мастер снова.");
        var environment = javaHome == null ? null : new Dictionary<string, string> { ["JAVA_HOME"] = javaHome };

        var sdk = settings.SdkRoot ?? DetectExistingSdk() ?? Path.Combine(AppSettings.RootDir, "sdk");
        settings.SdkRoot = sdk;
        Directory.CreateDirectory(sdk);
        progress.Report(new($"Android SDK: {sdk}"));

        // 1. cmdline-tools (sdkmanager / avdmanager)
        var sdkmanager = Path.Combine(sdk, "cmdline-tools", "latest", "bin", "sdkmanager.bat");
        if (!File.Exists(sdkmanager))
        {
            progress.Report(new("Загрузка Android command-line tools…", 0));
            var zipPath = Path.Combine(AppSettings.RootDir, "cmdline-tools.zip");
            await DownloadAsync(CmdlineToolsUrl, zipPath, progress, ct);

            progress.Report(new("Распаковка command-line tools…"));
            var tempDir = Path.Combine(AppSettings.RootDir, "clt-tmp");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            ZipFile.ExtractToDirectory(zipPath, tempDir);
            // В архиве папка «cmdline-tools»; sdkmanager требует раскладку <sdk>/cmdline-tools/latest/
            Directory.CreateDirectory(Path.Combine(sdk, "cmdline-tools"));
            Directory.Move(Path.Combine(tempDir, "cmdline-tools"), Path.Combine(sdk, "cmdline-tools", "latest"));
            Directory.Delete(tempDir, true);
            File.Delete(zipPath);
        }

        var acceptAll = string.Concat(Enumerable.Repeat("y\n", 100));

        // 2. Лицензии и пакеты SDK
        progress.Report(new("Принятие лицензий Android SDK…"));
        await RunBatAsync(sdkmanager, $"--sdk_root=\"{sdk}\" --licenses",
            acceptAll, environment, TimeSpan.FromMinutes(5), progress, ct);

        progress.Report(new("Установка platform-tools, emulator и system-image (1–3 ГБ, до 15 минут)…"));
        await RunBatAsync(sdkmanager,
            $"--sdk_root=\"{sdk}\" \"platform-tools\" \"emulator\" \"platforms;android-34\" \"{BuildToolsPackage}\" \"{SystemImage}\"",
            acceptAll, environment, TimeSpan.FromMinutes(45), progress, ct);

        // 3. Headless-AVD
        if (!Directory.Exists(settings.AvdDir))
        {
            progress.Report(new("Создание виртуального Android-устройства…"));
            var avdmanager = Path.Combine(sdk, "cmdline-tools", "latest", "bin", "avdmanager.bat");
            await RunBatAsync(avdmanager,
                $"create avd --force -n {settings.AvdName} -k \"{SystemImage}\" --device pixel_5",
                stdin: "no\n", // «Do you wish to create a custom hardware profile?»
                environment, TimeSpan.FromMinutes(3), progress, ct);

            var configIni = Path.Combine(settings.AvdDir, "config.ini");
            if (File.Exists(configIni))
                File.AppendAllText(configIni, "\nhw.keyboard=yes\ndisk.dataPartition.size=6442450944\n");
        }

        // 4. scrcpy
        if (settings.ScrcpyExe == null)
        {
            progress.Report(new($"Загрузка scrcpy v{ScrcpyVersion}…", 0));
            var zipPath = Path.Combine(AppSettings.RootDir, "scrcpy.zip");
            await DownloadAsync(ScrcpyUrl, zipPath, progress, ct);

            var scrcpyRoot = Path.Combine(AppSettings.RootDir, "scrcpy");
            if (Directory.Exists(scrcpyRoot)) Directory.Delete(scrcpyRoot, true);
            ZipFile.ExtractToDirectory(zipPath, scrcpyRoot);
            File.Delete(zipPath);

            var exe = Directory.GetFiles(scrcpyRoot, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("В архиве scrcpy не найден scrcpy.exe.");
            settings.ScrcpyDir = Path.GetDirectoryName(exe);
        }

        var missing = Validate();
        if (missing.Count > 0)
            throw new InvalidOperationException("После установки всё ещё отсутствуют: " + string.Join(", ", missing));

        settings.SetupCompleted = true;
        settings.Save();
        progress.Report(new("Готово — рантайм настроен.", 1));
    }

    private static bool TryFindJava(out string? javaHome)
    {
        javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome) && File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
            return true;

        javaHome = null;
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", "java")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            where!.WaitForExit(5000);
            return where.ExitCode == 0; // java есть в PATH — JAVA_HOME не обязателен
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is { } t)
                progress.Report(new("", (double)done / t));
        }
    }

    private static async Task RunBatAsync(
        string batPath, string args, string? stdin, IDictionary<string, string>? environment,
        TimeSpan timeout, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        // .bat исполняется только через cmd.exe — CreateProcess не запускает батники напрямую
        var result = await ProcessRunner.RunAsync(
            "cmd.exe", $"/c \"\"{batPath}\" {args}\"", timeout, stdin,
            line => progress.Report(new(line, Detail: true)), environment, ct);

        if (!result.Ok)
            throw new InvalidOperationException(
                $"{Path.GetFileName(batPath)} завершился с кодом {result.ExitCode}:\n{Tail(result.StdErr + result.StdOut, 2000)}");
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
