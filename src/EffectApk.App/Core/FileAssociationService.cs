using Microsoft.Win32;
using EffectApk.Interop;

namespace EffectApk.Core;

/// <summary>
/// Per-user регистрация обработчика .apk в HKCU\Software\Classes — без прав администратора.
/// Примечание: если у пользователя уже выбран другой обработчик (UserChoice),
/// Windows может один раз показать диалог «Каким образом вы хотите открыть этот файл?» —
/// достаточно выбрать EffectAPK и отметить «Всегда».
/// </summary>
public static class FileAssociationService
{
    private const string ProgId = "EffectApk.ApkFile";

    public static void RegisterForCurrentUser()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу.");

        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using (var extension = classes.CreateSubKey(".apk"))
            extension.SetValue(null, ProgId);

        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue(null, "Android-приложение (EffectAPK)");

            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exePath}\",0");

            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exePath}\" \"%1\"");
        }

        // Сообщаем проводнику, что ассоциации изменились (иначе — до перезахода в сессию)
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        Logger.Info(".apk ассоциирован с " + exePath);
    }

    public static bool IsRegistered()
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        var value = command?.GetValue(null) as string;
        return value != null && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }
}
