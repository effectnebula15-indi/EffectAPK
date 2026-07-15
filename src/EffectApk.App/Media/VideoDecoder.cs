using FFmpeg.AutoGen;
using EffectApk.Core;

namespace EffectApk.Media;

/// <summary>
/// Последний декодированный кадр в BGRA32. Буфер переиспользуется:
/// и запись (поток декодера), и чтение (UI) — строго под Gate.
/// </summary>
public sealed class VideoFrameBuffer
{
    public readonly object Gate = new();
    public byte[] Pixels = [];
    public int Width;
    public int Height;
    public bool HasFrame;
}

/// <summary>
/// H.264-декодер на FFmpeg (avcodec-61.dll и avutil-59.dll из дистрибутива scrcpy).
/// swscale scrcpy не поставляет, поэтому YUV420 → BGRA конвертируется вручную
/// (BT.601 limited range, параллельно по строкам — 1–2 мс на кадр 1080p).
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _lastWidth;
    private int _lastHeight;
    private volatile bool _disposed;

    public VideoFrameBuffer Output { get; } = new();

    /// <summary>Кадр записан в Output (вызывается на потоке декодера).</summary>
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

        // Один поток и LOW_DELAY: frame-threading добавил бы задержку в несколько кадров на ввод
        _context->thread_count = 1;
        _context->flags |= (int)ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        if (ffmpeg.avcodec_open2(_context, codec, null) < 0)
            throw new InvalidOperationException("FFmpeg: avcodec_open2 не удался.");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        Logger.Info($"FFmpeg-декодер инициализирован ({ffmpegDir})");
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
            try
            {
                ConvertToOutput(_frame);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private void ConvertToOutput(AVFrame* frame)
    {
        var width = frame->width;
        var height = frame->height;
        if (width <= 0 || height <= 0) return;

        var format = (AVPixelFormat)frame->format;
        if (format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
        {
            Logger.Error("Неожиданный формат кадра: " + format);
            return;
        }

        lock (Output.Gate)
        {
            var needed = width * height * 4;
            if (Output.Pixels.Length < needed)
                Output.Pixels = new byte[needed];
            Output.Width = width;
            Output.Height = height;
            Output.HasFrame = true;

            var strideY = frame->linesize[0];
            var strideU = frame->linesize[1];
            var strideV = frame->linesize[2];

            fixed (byte* dst = Output.Pixels)
            {
                // Указатели нельзя захватывать в лямбду — передаём как nint
                nint pY = (nint)frame->data[0];
                nint pU = (nint)frame->data[1];
                nint pV = (nint)frame->data[2];
                nint pDst = (nint)dst;

                Parallel.For(0, height, row =>
                {
                    var yRow = (byte*)pY + row * strideY;
                    var uRow = (byte*)pU + (row >> 1) * strideU;
                    var vRow = (byte*)pV + (row >> 1) * strideV;
                    var outRow = (byte*)pDst + (long)row * width * 4;

                    for (var x = 0; x < width; x++)
                    {
                        // BT.601 limited range: C=Y-16, D=U-128, E=V-128
                        var c = 298 * (yRow[x] - 16);
                        var d = uRow[x >> 1] - 128;
                        var e = vRow[x >> 1] - 128;

                        var r = (c + 409 * e + 128) >> 8;
                        var g = (c - 100 * d - 208 * e + 128) >> 8;
                        var b = (c + 516 * d + 128) >> 8;

                        outRow[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        outRow[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        outRow[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        outRow[3] = 255;
                        outRow += 4;
                    }
                });
            }
        }

        if (width != _lastWidth || height != _lastHeight)
        {
            _lastWidth = width;
            _lastHeight = height;
            Logger.Info($"Видеопоток: новый размер {width}x{height}");
            SizeChanged?.Invoke(width, height);
        }

        FrameReady?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
