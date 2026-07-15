using EffectApk.Core;
using EffectApk.UI;

namespace EffectApk.Sessions;

/// <summary>Живая связка «APK → scrcpy-клиент → окно» для одного запущенного приложения.</summary>
public sealed record AppSession(ApkInfo Apk, ScrcpyClient Scrcpy, AppHostWindow Window);
