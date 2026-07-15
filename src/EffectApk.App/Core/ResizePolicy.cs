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
        // Никакого округления: wm size принимает любые значения, а выравнивание под кодек
        // (кратность 8) делает сам scrcpy-server. Любое наше округление приводило к дрейфу
        // пропорций между окном и дисплеем — тонким чёрным полосам по краям.
        var scale = Math.Min(1.0, 1920.0 / Math.Max(clientWidthPx, clientHeightPx));
        var width = Math.Max(320, (int)Math.Round(clientWidthPx * scale));
        var height = Math.Max(320, (int)Math.Round(clientHeightPx * scale));
        return (width, height);
    }

    /// <summary>
    /// Плотность, при которой масштаб UI постоянен: пропорциональна короткой стороне
    /// с опорой на «эталонный телефон» 1080 px / 320 dpi. Ширина в dp тогда всегда ~540:
    /// раскладка приложения не зависит от размера окна, а рендер идёт 1:1 к его пикселям.
    /// </summary>
    public static int ComputeDensity(int width, int height)
    {
        var shortSide = Math.Min(width, height);
        return Math.Clamp((int)Math.Round(320.0 * shortSide / 1080), 120, 640);
    }
}
