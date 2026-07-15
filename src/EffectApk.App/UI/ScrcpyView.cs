using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EffectApk.Core;
using EffectApk.Sessions;

namespace EffectApk.UI;

/// <summary>
/// Собственный рендер потока scrcpy: WriteableBitmap + прокидывание мыши/клавиатуры
/// в control-сокет. Никаких чужих окон — обычный WPF-элемент, растянутый на клиентскую область.
/// </summary>
public sealed class ScrcpyView : Grid
{
    // Android-коды для спецклавиш (printable-символы идут текстом через TextInput)
    private static readonly Dictionary<Key, int> KeyMap = new()
    {
        [Key.Enter] = 66,
        [Key.Back] = 67,
        [Key.Tab] = 61,
        [Key.Escape] = 4,    // Android BACK — привычнее всего
        [Key.Delete] = 112,
        [Key.Home] = 122,
        [Key.End] = 123,
        [Key.PageUp] = 92,
        [Key.PageDown] = 93,
        [Key.Left] = 21,
        [Key.Up] = 19,
        [Key.Right] = 22,
        [Key.Down] = 20,
    };

    private readonly ScrcpyClient _client;
    private readonly Image _image;
    private WriteableBitmap? _bitmap;
    private int _renderPending;
    private bool _touchActive;

    public ScrcpyView(ScrcpyClient client)
    {
        _client = client;
        Background = Brushes.Black;
        Focusable = true;
        FocusVisualStyle = null;

        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        Children.Add(_image);

        _client.Decoder.FrameReady += OnFrameReady;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        PreviewKeyDown += (_, e) => OnKey(e, down: true);
        PreviewKeyUp += (_, e) => OnKey(e, down: false);
        PreviewTextInput += OnTextInput;
    }

    // ---------- Рендер ----------

    private void OnFrameReady()
    {
        // Схлопываем кадры: пока UI не отрисовал предыдущий, новые только обновляют буфер
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        Dispatcher.BeginInvoke(RenderLatestFrame, DispatcherPriority.Render);
    }

    private void RenderLatestFrame()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        var decoder = _client.Decoder;
        var width = decoder.Width;
        var height = decoder.Height;
        if (width <= 0 || height <= 0) return;

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
            Logger.Info($"Рендер: пересоздан буфер {width}x{height}");
        }

        // Декодер конвертирует YUV прямо в back-buffer — промежуточных копий нет
        decoder.RenderTo(_bitmap);
    }

    // ---------- Ввод ----------

    private (int X, int Y) ToVideoCoords(Point p)
    {
        var w = _client.VideoWidth;
        var h = _client.VideoHeight;
        var x = (int)Math.Clamp(p.X / Math.Max(1.0, ActualWidth) * w, 0, Math.Max(0, w - 1));
        var y = (int)Math.Clamp(p.Y / Math.Max(1.0, ActualHeight) * h, 0, Math.Max(0, h - 1));
        return (x, y);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var (x, y) = ToVideoCoords(e.GetPosition(this));
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                _touchActive = true;
                CaptureMouse();
                _client.SendTouch(ScrcpyClient.ActionDown, x, y, pressure: 0xFFFF, actionButton: 1, buttons: 1);
                break;
            case MouseButton.Right:
                _client.SendBackOrScreenOn(ScrcpyClient.ActionDown);
                break;
            case MouseButton.Middle:
                _client.SendKeycode(ScrcpyClient.ActionDown, 3); // HOME
                break;
        }
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_touchActive) return;
        var (x, y) = ToVideoCoords(e.GetPosition(this));
        _client.SendTouch(ScrcpyClient.ActionMove, x, y, pressure: 0xFFFF, actionButton: 0, buttons: 1);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var (x, y) = ToVideoCoords(e.GetPosition(this));
        switch (e.ChangedButton)
        {
            case MouseButton.Left when _touchActive:
                _touchActive = false;
                ReleaseMouseCapture();
                _client.SendTouch(ScrcpyClient.ActionUp, x, y, pressure: 0, actionButton: 1, buttons: 0);
                break;
            case MouseButton.Right:
                _client.SendBackOrScreenOn(ScrcpyClient.ActionUp);
                break;
            case MouseButton.Middle:
                _client.SendKeycode(ScrcpyClient.ActionUp, 3);
                break;
        }
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var (x, y) = ToVideoCoords(e.GetPosition(this));
        _client.SendScroll(x, y, hScroll: 0f, vScroll: e.Delta / 120f);
        e.Handled = true;
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!KeyMap.TryGetValue(key, out var androidKeycode)) return;
        _client.SendKeycode(down ? ScrcpyClient.ActionDown : ScrcpyClient.ActionUp, androidKeycode);
        e.Handled = true;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        // Спецсимволы уже обработаны как keycode; остальное шлём текстом (включая пробел и Unicode)
        if (string.IsNullOrEmpty(e.Text) || e.Text is "\r" or "\n" or "\b" or "\t" or "\x1b") return;
        _client.SendText(e.Text);
        e.Handled = true;
    }
}
