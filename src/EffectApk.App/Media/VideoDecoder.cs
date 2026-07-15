using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Imaging;
using FFmpeg.AutoGen;
using EffectApk.Core;

namespace EffectApk.Media;

/// <summary>
/// H.264-декодер на FFmpeg (avcodec-61.dll и avutil-59.dll из дистрибутива scrcpy).
/// swscale scrcpy не поставляет, поэтому YUV420 → BGRA конвертируется вручную
/// (BT.601 limited range) — сразу в back-buffer WriteableBitmap, без промежуточных копий.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly object _gate = new();        // защищает _pending: держится микросекунды
    private readonly object _renderGate = new();  // UI ↔ Dispose: конверсия не блокирует декодер
    private AVCodecContext* _context;
    private AVFrame* _frame;        // рабочий кадр декодера
    private AVFrame* _pending;      // последний готовый кадр (ref, не копия пикселей)
    private AVFrame* _renderFrame;  // кадр, который сейчас конвертирует UI
    private AVPacket* _packet;
    private int _lastWidth;
    private int _lastHeight;
    private volatile bool _disposed;

    /// <summary>Размер последнего декодированного кадра.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Кадр готов к отрисовке (вызывается на потоке декодера).</summary>
    public event Action? FrameReady;

    /// <summary>Размер потока изменился (wm size на устройстве и т.п.).</summary>
    public event Action<int, int>? SizeChanged;

    public VideoDecoder(string ffmpegDir)
    {
        ffmpeg.RootPath = ffmpegDir;

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("FFmpeg: H.264-декодер недоступен.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context == null)
            throw new InvalidOperationException("FFmpeg: не удалось создать контекст декодера.");

        // Slice-threading (в отличие от frame-threading) ускоряет декод, не добавляя
        // задержки в кадрах — для интерактивного зеркалирования это критично
        _context->thread_count = Math.Min(4, Environment.ProcessorCount);
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        _context->flags |= (int)ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->flags2 |= (int)ffmpeg.AV_CODEC_FLAG2_FAST;

        if (ffmpeg.avcodec_open2(_context, codec, null) < 0)
            throw new InvalidOperationException("FFmpeg: avcodec_open2 не удался.");

        _frame = ffmpeg.av_frame_alloc();
        _pending = ffmpeg.av_frame_alloc();
        _renderFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        Logger.Info($"FFmpeg-декодер: {_context->thread_count} потоков (slice), {ffmpegDir}");
    }

    /// <summary>Скармливает один пакет протокола scrcpy (конфигурационные SPS/PPS — тоже сюда).</summary>
    public void Feed(ReadOnlySpan<byte> data, bool isConfig = false)
    {
        if (_disposed || data.IsEmpty) return;

        // Config-пакет (SPS/PPS) приходит при каждом рестарте энкодера (смена разрешения) —
        // сбрасываем состояние декодера, иначе первый пакет нового потока даёт INVALIDDATA
        if (isConfig)
            ffmpeg.avcodec_flush_buffers(_context);

        if (ffmpeg.av_new_packet(_packet, data.Length) < 0) return;
        data.CopyTo(new Span<byte>(_packet->data, data.Length));

        var sendResult = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            Logger.Debug($"avcodec_send_packet: {sendResult}");
            return;
        }

        while (ffmpeg.avcodec_receive_frame(_context, _frame) >= 0)
        {
            var width = _frame->width;
            var height = _frame->height;

            // av_frame_ref — refcount, а не копия пикселей: передача кадра в UI бесплатна
            lock (_gate)
            {
                if (_disposed) { ffmpeg.av_frame_unref(_frame); return; }
                ffmpeg.av_frame_unref(_pending);
                ffmpeg.av_frame_ref(_pending, _frame);
                Width = width;
                Height = height;
            }
            ffmpeg.av_frame_unref(_frame);

            if (width != _lastWidth || height != _lastHeight)
            {
                _lastWidth = width;
                _lastHeight = height;
                Logger.Info($"Видеопоток: новый размер {width}x{height}");
                SizeChanged?.Invoke(width, height);
            }

            FrameReady?.Invoke();
        }
    }

    /// <summary>
    /// Конвертирует последний кадр прямо в back-buffer битмапа. Только UI-поток.
    /// Возвращает false, если кадра нет или размер не совпадает с битмапом.
    /// </summary>
    public bool RenderTo(WriteableBitmap bitmap)
    {
        // _renderGate держится всю конверсию — Dispose дождётся её завершения,
        // а поток декодера при этом свободен (ему нужен только _gate)
        lock (_renderGate)
        {
            lock (_gate)
            {
                if (_disposed || _pending == null || _pending->data[0] == null) return false;
                ffmpeg.av_frame_unref(_renderFrame);
                ffmpeg.av_frame_ref(_renderFrame, _pending); // refcount, не копия пикселей
            }

            var frame = _renderFrame;
            var width = frame->width;
            var height = frame->height;
            if (width <= 0 || height <= 0) return false;
            if (bitmap.PixelWidth != width || bitmap.PixelHeight != height) return false;

            var format = (AVPixelFormat)frame->format;
            if (format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
            {
                Logger.Error("Неожиданный формат кадра: " + format);
                return false;
            }

            bitmap.Lock();
            try
            {
                ConvertYuv420ToBgra(frame, (byte*)bitmap.BackBuffer, bitmap.BackBufferStride, width, height);
                bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                bitmap.Unlock();
            }
            return true;
        }
    }

    private static void ConvertYuv420ToBgra(AVFrame* frame, byte* destination, int destinationStride, int width, int height)
    {
        var strideY = frame->linesize[0];
        var strideU = frame->linesize[1];
        var strideV = frame->linesize[2];
        // Указатели нельзя захватывать в лямбду — передаём как nint
        nint pY = (nint)frame->data[0];
        nint pU = (nint)frame->data[1];
        nint pV = (nint)frame->data[2];
        nint pDst = (nint)destination;

        // Разбиение по диапазонам строк: на 800+ строк поштучный Parallel.For
        // тратит больше времени на планирование, чем на саму конвертацию
        var chunk = Math.Max(16, height / (Environment.ProcessorCount * 2));
        var partitioner = Partitioner.Create(0, height, chunk);

        Parallel.ForEach(partitioner, range =>
        {
            for (var row = range.Item1; row < range.Item2; row++)
            {
                var y = (byte*)pY + row * strideY;
                var u = (byte*)pU + (row >> 1) * strideU;
                var v = (byte*)pV + (row >> 1) * strideV;
                var dst = (byte*)pDst + (long)row * destinationStride;

                var x = 0;
                // Пара пикселей на одну загрузку UV: вдвое меньше чтений цветности
                // и вдвое меньше вычислений коэффициентов
                for (; x + 1 < width; x += 2)
                {
                    var uu = u[x >> 1] - 128;
                    var vv = v[x >> 1] - 128;
                    var rC = 409 * vv + 128;
                    var gC = -100 * uu - 208 * vv + 128;
                    var bC = 516 * uu + 128;

                    var c = 298 * (y[x] - 16);
                    dst[0] = Clamp(c + bC);
                    dst[1] = Clamp(c + gC);
                    dst[2] = Clamp(c + rC);
                    dst[3] = 255;

                    c = 298 * (y[x + 1] - 16);
                    dst[4] = Clamp(c + bC);
                    dst[5] = Clamp(c + gC);
                    dst[6] = Clamp(c + rC);
                    dst[7] = 255;
                    dst += 8;
                }

                if (x < width)
                {
                    var uu = u[x >> 1] - 128;
                    var vv = v[x >> 1] - 128;
                    var c = 298 * (y[x] - 16);
                    dst[0] = Clamp(c + 516 * uu + 128);
                    dst[1] = Clamp(c - 100 * uu - 208 * vv + 128);
                    dst[2] = Clamp(c + 409 * vv + 128);
                    dst[3] = 255;
                }
            }
        });
    }

    private static byte Clamp(int value)
    {
        value >>= 8;
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
            _disposed = true; // новые RenderTo сразу вернут false

        // Ждём, пока UI закончит текущую конверсию, иначе — use-after-free
        lock (_renderGate)
        lock (_gate)
        {
            var renderFrame = _renderFrame;
            ffmpeg.av_frame_free(&renderFrame);
            _renderFrame = null;

            var pending = _pending;
            ffmpeg.av_frame_free(&pending);
            _pending = null;

            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;

            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;

            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }
}
