namespace EffectApk.Core;

/// <summary>
/// Общая «сетка», на которой живут и окно, и виртуальный Android-дисплей:
/// обе стороны всегда кратны 8 и не меньше 320. Окно снапится к этой сетке при
/// каждом изменении размера, а дисплей задаётся ровно в пикселях клиентской
/// области — поэтому окно и дисплей совпадают 1:1, без масштабирования.
/// </summary>
public static class ResizePolicy
{
    /// <summary>
    /// Минимальная сторона дисплея: ниже многие приложения ломают вёрстку,
    /// а H.264-энкодер отказывается работать.
    /// </summary>
    public const int MinSide = 320;

    // Размер дисплея ОБЯЗАН быть кратен 8: scrcpy-server выравнивает под кодек размер
    // видеопотока (округляя к ближайшему кратному 8), а сам дисплей не трогает. Если
    // задать дисплей 684x881, поток станет 688x880 — картинка дисплея кодируется со
    // сдвигом/обрезкой, отчего контент «съезжает», а координаты касаний промахиваются.
    // Окно снапится к той же сетке (SnapWindowSide), так что обычно клиентская область
    // уже кратна 8 и дисплей равен ей точно; округлять вниз приходится только «чужие»
    // размеры — например, клиентскую область максимизированного окна.
    public static (int Width, int Height) ComputeAndroidResolution(int clientWidthPx, int clientHeightPx) =>
        (AlignFloor8(clientWidthPx), AlignFloor8(clientHeightPx));

    /// <summary>Сторона окна на «сетке дисплея»: ближайшее кратное 8, минимум 320.</summary>
    public static int SnapWindowSide(double px) =>
        Math.Max(MinSide, (int)Math.Round(px / 8.0) * 8);

    private static int AlignFloor8(int px) => Math.Max(MinSide, px / 8 * 8);

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
