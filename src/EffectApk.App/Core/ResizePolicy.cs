namespace EffectApk.Core;

/// <summary>
/// Политика Shift+resize: во что превратить размер клиентской области окна (физические пиксели)
/// при смене разрешения виртуального Android-дисплея.
/// </summary>
public static class ResizePolicy
{
    // TODO(пользователь): это осознанная точка настройки — см. объяснение в README и в чате.
    // Текущая политика по умолчанию:
    //   1:1 к пикселям окна, длинная сторона ограничена 1920 (нагрузка на кодек),
    //   стороны округлены вниз до кратного 16 (требование H.264-энкодера),
    //   минимум 320 px (иначе многие приложения ломают вёрстку).
    public static (int Width, int Height) ComputeAndroidResolution(int clientWidthPx, int clientHeightPx)
    {
        var scale = Math.Min(1.0, 1920.0 / Math.Max(clientWidthPx, clientHeightPx));
        var width = Math.Max(320, (int)(clientWidthPx * scale) & ~15);
        var height = Math.Max(320, (int)(clientHeightPx * scale) & ~15);
        return (width, height);
    }
}
