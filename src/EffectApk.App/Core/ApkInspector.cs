using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace EffectApk.Core;

public sealed record ApkInfo(
    string ApkPath,
    string PackageName,
    long VersionCode,
    string Label,
    IReadOnlyList<string> IconEntries);

/// <summary>Читает метаданные APK через «aapt2 dump badging» и извлекает иконку прямо из zip-структуры APK.</summary>
public sealed class ApkInspector(string aapt2Path)
{
    public async Task<ApkInfo> InspectAsync(string apkPath, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(aapt2Path, $"dump badging \"{apkPath}\"", TimeSpan.FromSeconds(60), ct: ct);
        // aapt2 может вернуть ненулевой код из-за некритичных предупреждений — важен только распарсенный package
        var text = result.StdOut;

        var package = Regex.Match(text, @"package: name='([^']+)'");
        if (!package.Success)
            throw new InvalidOperationException($"Файл не похож на корректный APK.\n{Tail(result.StdErr, 500)}");

        long.TryParse(Regex.Match(text, @"versionCode='(\d+)'").Groups[1].Value, out var versionCode);

        var label = Regex.Match(text, @"application-label:'([^']+)'").Groups[1].Value;
        if (label.Length == 0)
            label = Path.GetFileNameWithoutExtension(apkPath);

        // application-icon-<density>:'res/...' — сортируем по плотности, чтобы взять самую крупную
        var icons = Regex.Matches(text, @"application-icon-(\d+):'([^']+)'")
            .OrderByDescending(m => int.Parse(m.Groups[1].Value))
            .Select(m => m.Groups[2].Value)
            .ToList();

        return new ApkInfo(apkPath, package.Groups[1].Value, versionCode, label, icons);
    }

    /// <summary>Иконка окна. Адаптивные XML-иконки не декодируем — берём первый PNG или крупнейший ic_launcher*.png.</summary>
    public static BitmapSource? TryExtractIcon(ApkInfo apk)
    {
        try
        {
            using var zip = ZipFile.OpenRead(apk.ApkPath);

            foreach (var entryName in apk.IconEntries.Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                if (zip.GetEntry(entryName) is { } entry)
                    return Decode(entry);

            var fallback = zip.Entries
                .Where(e => e.FullName.StartsWith("res/")
                            && e.Name.Contains("ic_launcher")
                            && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();
            return fallback == null ? null : Decode(fallback);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось извлечь иконку из APK", ex);
            return null;
        }
    }

    private static BitmapSource Decode(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
