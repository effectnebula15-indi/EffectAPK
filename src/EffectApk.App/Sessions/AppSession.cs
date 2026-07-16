using EffectApk.Core;
using EffectApk.UI;

namespace EffectApk.Sessions;

/// <summary>
/// Живая связка «APK → scrcpy-клиент → окно» для одного запущенного приложения.
/// Scrcpy изменяемый: смена разрешения дисплея = новая scrcpy-сессия (см. App.SwapSessionAsync).
/// </summary>
public sealed class AppSession(ApkInfo apk, ScrcpyClient scrcpy, AppHostWindow window)
{
    public ApkInfo Apk { get; } = apk;
    public ScrcpyClient Scrcpy { get; set; } = scrcpy;
    public AppHostWindow Window { get; } = window;
}
