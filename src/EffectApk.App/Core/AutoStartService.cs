using Microsoft.Win32;

namespace EffectApk.Core;

/// <summary>
/// Автозагрузка при входе в Windows: HKCU\...\Run, per-user, без прав администратора.
/// Запускаемся с флагом --background: тихий старт в трей, без мастера и без всплывающих подсказок.
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EffectApk";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
            Logger.Info("Автозагрузка включена");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info("Автозагрузка выключена");
        }
    }
}
