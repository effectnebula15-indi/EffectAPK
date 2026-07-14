using EffectApk.Core;
using EffectApk.UI;

namespace EffectApk.Sessions;

/// <summary>Живая связка «APK → scrcpy-процесс → окно» для одного запущенного приложения.</summary>
public sealed record AppSession(ApkInfo Apk, ScrcpySession Scrcpy, AppHostWindow Window);
