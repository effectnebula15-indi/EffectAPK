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
        // Размер дисплея ОБЯЗАН быть кратен 8: scrcpy-server выравнивает под кодек размер
        // видеопотока (округляя к ближайшему кратному 8), а сам дисплей не трогает. Если
        // задать дисплей 684x881, поток станет 688x880 — картинка дисплея кодируется со
        // сдвигом/обрезкой, отчего контент «съезжает», а координаты касаний промахиваются.
        // Кратный 8 дисплей делает поток тождественным дисплею.
        var scale = Math.Min(1.0, 1920.0 / Math.Max(clientWidthPx, clientHeightPx));
        return (Align8(clientWidthPx * scale), Align8(clientHeightPx * scale));
    }

    private static int Align8(double value) =>
        Math.Max(320, (int)Math.Round(value / 8.0) * 8);

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
