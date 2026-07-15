using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using EffectApk.Core;
using EffectApk.Media;
using NAudio.Wave;

namespace EffectApk.Sessions;

/// <summary>
/// Собственный клиент протокола scrcpy — scrcpy.exe и SDL больше не используются.
/// Схема: adb push scrcpy-server → adb forward tcp:PORT localabstract:scrcpy_SCID →
/// adb shell app_process com.genymobile.scrcpy.Server → три сокета в порядке video, audio, control.
/// Видео (H.264) декодируется в VideoDecoder, аудио (raw PCM 48кГц/16бит/стерео) играет через NAudio,
/// ввод шлётся бинарными сообщениями в control-сокет.
/// </summary>
public sealed class ScrcpyClient : IDisposable
{
    private const int ConnectTimeoutSeconds = 20;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _controlGate = new();
    private AdbService _adb = null!;
    private Process? _serverProcess;
    private TcpClient? _videoSocket;
    private TcpClient? _audioSocket;
    private TcpClient? _controlSocket;
    private NetworkStream? _controlStream;
    private int _localPort;
    private bool _videoLoopOwnsDecoder;
    private int _exitedRaised;

    public VideoDecoder Decoder { get; private set; } = null!;
    public int? DisplayId { get; private set; }

    /// <summary>
    /// Текущий размер видеопотока — обязательная система координат для touch/scroll:
    /// сервер ОТБРАСЫВАЕТ событие, если screen_size в сообщении не равен его videoSize
    /// (защита от координат, посчитанных до поворота/ресайза).
    /// </summary>
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    public bool HasExited => _cts.IsCancellationRequested;
    public event Action? Exited;

    public static async Task<ScrcpyClient> StartAsync(
        AppSettings settings, string packageName, AdbService adb,
        int displayWidth, int displayHeight, CancellationToken ct)
    {
        var client = new ScrcpyClient { _adb = adb };
        try
        {
            await client.StartCoreAsync(settings, packageName, displayWidth, displayHeight, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task StartCoreAsync(
        AppSettings settings, string packageName, int displayWidth, int displayHeight, CancellationToken ct)
    {
        var scrcpyDir = settings.ScrcpyDir
            ?? throw new InvalidOperationException("scrcpy не найден — запустите мастер настройки.");
        var serverJar = Path.Combine(scrcpyDir, "scrcpy-server");
        if (!File.Exists(serverJar))
            throw new InvalidOperationException("Не найден scrcpy-server: " + serverJar);

        Decoder = new VideoDecoder(scrcpyDir);
        Decoder.SizeChanged += (w, h) => { VideoWidth = w; VideoHeight = h; };

        await _adb.PushAsync(serverJar, "/data/local/tmp/scrcpy-server.jar");

        var scid = $"{Random.Shared.Next(1, int.MaxValue):x8}";
        _localPort = GetFreeTcpPort();
        await _adb.ForwardAsync(_localPort, $"localabstract:scrcpy_{scid}");

        var displayIdFromLog = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverCommand =
            "CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server " +
            RuntimeBootstrapper.ScrcpyVersion +
            $" scid={scid} log_level=info" +
            " video=true video_codec=h264 video_bit_rate=8000000 max_size=0 max_fps=60" +
            " audio=true audio_codec=raw" +
            " control=true tunnel_forward=true cleanup=true" +
            " send_device_meta=false send_frame_meta=true send_codec_meta=true send_dummy_byte=true" +
            " clipboard_autosync=false" +
            // Плотность сразу правильная для этого размера — иначе при точном совпадении
            // окна с дисплеем пост-стартовая синхронизация не сработает и не поправит её
            $" new_display={displayWidth}x{displayHeight}/{ResizePolicy.ComputeDensity(displayWidth, displayHeight)}" +
            $" start_app={packageName}";

        _serverProcess = _adb.StartShellDetached(serverCommand, line =>
        {
            Logger.Debug("[scrcpy-server] " + line);
            var match = Regex.Match(line, @"[Dd]isplay.*?id[=:\s]+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && id != 0)
                displayIdFromLog.TrySetResult(id);
        });
        _serverProcess.Exited += (_, _) => RaiseExited();

        // Сокеты принимаются сервером строго по порядку: video, audio, control.
        // На первом сервер шлёт dummy-байт — по нему отличаем «adb принял, сервера ещё нет».
        _videoSocket = await ConnectFirstSocketAsync(ct);
        _audioSocket = await ConnectSocketAsync(ct);
        _controlSocket = await ConnectSocketAsync(ct);
        _controlStream = _controlSocket.GetStream();

        // Метаданные видео: codecId(4) + width(4) + height(4), big-endian
        var videoStream = _videoSocket.GetStream();
        var meta = new byte[12];
        await videoStream.ReadExactlyAsync(meta, ct);
        VideoWidth = BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(4));
        VideoHeight = BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(8));
        Logger.Info($"scrcpy-клиент: видеопоток {VideoWidth}x{VideoHeight}, порт {_localPort}");

        _videoLoopOwnsDecoder = true;
        _ = Task.Run(() => VideoLoopAsync(videoStream));
        _ = Task.Run(() => AudioLoopAsync(_audioSocket.GetStream()));
        _ = Task.Run(() => ControlDrainLoopAsync(_controlStream));

        var finished = await Task.WhenAny(displayIdFromLog.Task, Task.Delay(3000, ct));
        DisplayId = finished == displayIdFromLog.Task
            ? displayIdFromLog.Task.Result
            : await _adb.FindScrcpyDisplayIdAsync();
        Logger.Info($"scrcpy-клиент запущен: pkg={packageName}, displayId={DisplayId?.ToString() ?? "не определён"}");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<TcpClient> ConnectFirstSocketAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(ConnectTimeoutSeconds))
                throw new TimeoutException("scrcpy-server не поднялся за отведённое время (см. лог [scrcpy-server]).");
            if (_serverProcess is { HasExited: true })
                throw new InvalidOperationException("scrcpy-server завершился при запуске — смотрите лог [scrcpy-server].");

            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, _localPort, ct);
                var dummy = new byte[1];
                var read = await tcp.GetStream().ReadAsync(dummy, ct);
                if (read == 1)
                {
                    tcp.NoDelay = true;
                    return tcp;
                }
            }
            catch (SocketException) { }
            catch (IOException) { }

            tcp?.Dispose();
            await Task.Delay(100, ct);
        }
    }

    private async Task<TcpClient> ConnectSocketAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _localPort, ct);
        tcp.NoDelay = true;
        return tcp;
    }

    // ---------- Потоки чтения ----------

    private async Task VideoLoopAsync(NetworkStream stream)
    {
        var header = new byte[12]; // ptsAndFlags(8) + packetSize(4)
        var payload = new byte[256 * 1024];
        long packets = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _cts.Token);
                var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header);
                var isConfig = (ptsAndFlags & (1UL << 63)) != 0; // PACKET_FLAG_CONFIG
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                if (size <= 0 || size > 16 * 1024 * 1024)
                    throw new InvalidDataException("Некорректный размер видеопакета: " + size);
                if (payload.Length < size)
                    payload = new byte[size];
                await stream.ReadExactlyAsync(payload.AsMemory(0, size), _cts.Token);

                Decoder.Feed(payload.AsSpan(0, size), isConfig);
                if (++packets % 600 == 0)
                    Logger.Debug($"видео: {packets} пакетов, поток {VideoWidth}x{VideoHeight}");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException)
        {
            Logger.Info("Видеопоток завершён (" + ex.GetType().Name + ")");
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка видеопотока", ex);
        }
        finally
        {
            Decoder.Dispose();
            RaiseExited();
        }
    }

    private async Task AudioLoopAsync(NetworkStream stream)
    {
        try
        {
            // Метаданные аудио: 4 байта codecId; 0x00000000 = аудио отключено, 0x00000001 = ошибка на устройстве
            var meta = new byte[4];
            await stream.ReadExactlyAsync(meta, _cts.Token);
            var codecId = BinaryPrimitives.ReadUInt32BigEndian(meta);
            if (codecId is 0u or 1u)
            {
                Logger.Info($"Аудио недоступно (codecId={codecId}) — продолжаем без звука");
                return;
            }

            // raw PCM: 48000 Гц, 16 бит, стерео (константы scrcpy)
            var provider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            using var output = new WaveOutEvent { DesiredLatency = 150 };
            output.Init(provider);
            output.Play();
            Logger.Info("Аудио: raw PCM 48кГц/стерео через WaveOut");

            var header = new byte[12];
            var payload = new byte[64 * 1024];
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _cts.Token);
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                if (size <= 0 || size > 1024 * 1024)
                    throw new InvalidDataException("Некорректный размер аудиопакета: " + size);
                if (payload.Length < size)
                    payload = new byte[size];
                await stream.ReadExactlyAsync(payload.AsMemory(0, size), _cts.Token);
                provider.AddSamples(payload, 0, size);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException)
        {
            Logger.Info("Аудиопоток завершён");
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка аудио — продолжаем без звука", ex);
        }
    }

    private async Task ControlDrainLoopAsync(NetworkStream stream)
    {
        // Сервер может слать сообщения (clipboard и т.п.) — вычитываем и игнорируем,
        // иначе переполнится TCP-буфер и сервер заблокируется
        var sink = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(sink, _cts.Token);
                if (read == 0) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // ---------- Отправка ввода (бинарный протокол scrcpy, big-endian) ----------

    private const byte TypeInjectKeycode = 0;
    private const byte TypeInjectText = 1;
    private const byte TypeInjectTouch = 2;
    private const byte TypeInjectScroll = 3;
    private const byte TypeBackOrScreenOn = 4;

    public const byte ActionDown = 0;
    public const byte ActionUp = 1;
    public const byte ActionMove = 2;

    private void SendControl(ReadOnlySpan<byte> message)
    {
        var stream = _controlStream;
        if (stream == null || _cts.IsCancellationRequested) return;
        try
        {
            lock (_controlGate)
                stream.Write(message);
        }
        catch (Exception ex)
        {
            Logger.Debug("control send: " + ex.Message);
        }
    }

    /// <summary>Touch/мышь. pointerId -1 (POINTER_ID_MOUSE) + buttons — как у настоящего scrcpy.</summary>
    public void SendTouch(byte action, int x, int y, ushort pressure, uint actionButton, uint buttons)
    {
        int w = VideoWidth, h = VideoHeight;
        if (w <= 0 || h <= 0) return;

        Span<byte> msg = stackalloc byte[32];
        msg[0] = TypeInjectTouch;
        msg[1] = action;
        BinaryPrimitives.WriteUInt64BigEndian(msg[2..], 0xFFFF_FFFF_FFFF_FFFF); // POINTER_ID_MOUSE
        BinaryPrimitives.WriteInt32BigEndian(msg[10..], x);
        BinaryPrimitives.WriteInt32BigEndian(msg[14..], y);
        BinaryPrimitives.WriteUInt16BigEndian(msg[18..], (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(msg[20..], (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(msg[22..], pressure);
        BinaryPrimitives.WriteUInt32BigEndian(msg[24..], actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(msg[28..], buttons);
        SendControl(msg);
    }

    /// <summary>Колесо: значения — «плавающие» в фиксированной точке i16 (1.0 = один щелчок).</summary>
    public void SendScroll(int x, int y, float hScroll, float vScroll)
    {
        int w = VideoWidth, h = VideoHeight;
        if (w <= 0 || h <= 0) return;

        Span<byte> msg = stackalloc byte[21];
        msg[0] = TypeInjectScroll;
        BinaryPrimitives.WriteInt32BigEndian(msg[1..], x);
        BinaryPrimitives.WriteInt32BigEndian(msg[5..], y);
        BinaryPrimitives.WriteUInt16BigEndian(msg[9..], (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(msg[11..], (ushort)h);
        BinaryPrimitives.WriteInt16BigEndian(msg[13..], FloatToI16Fp(hScroll));
        BinaryPrimitives.WriteInt16BigEndian(msg[15..], FloatToI16Fp(vScroll));
        BinaryPrimitives.WriteUInt32BigEndian(msg[17..], 0);
        SendControl(msg);
    }

    private static short FloatToI16Fp(float value) =>
        (short)Math.Clamp((int)(value * 0x8000), short.MinValue, short.MaxValue);

    public void SendKeycode(byte action, int androidKeycode, int metaState = 0)
    {
        Span<byte> msg = stackalloc byte[14];
        msg[0] = TypeInjectKeycode;
        msg[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(msg[2..], androidKeycode);
        BinaryPrimitives.WriteInt32BigEndian(msg[6..], 0); // repeat
        BinaryPrimitives.WriteInt32BigEndian(msg[10..], metaState);
        SendControl(msg);
    }

    public void SendText(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length is 0 or > 300) return;
        var msg = new byte[5 + utf8.Length];
        msg[0] = TypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(1), utf8.Length);
        utf8.CopyTo(msg, 5);
        SendControl(msg);
    }

    /// <summary>Android BACK (или включение экрана) — на него замаплен правый клик.</summary>
    public void SendBackOrScreenOn(byte action)
    {
        Span<byte> msg = stackalloc byte[2];
        msg[0] = TypeBackOrScreenOn;
        msg[1] = action;
        SendControl(msg);
    }

    // ---------- Завершение ----------

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
        _cts.Cancel();
        Exited?.Invoke();
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        _cts.Cancel();
        try { _videoSocket?.Close(); } catch { }
        try { _audioSocket?.Close(); } catch { }
        try { _controlSocket?.Close(); } catch { }
        try
        {
            if (_serverProcess is { HasExited: false })
                _serverProcess.Kill(entireProcessTree: true);
        }
        catch { }
        if (_localPort != 0)
            _ = _adb.RemoveForwardAsync(_localPort);
        if (!_videoLoopOwnsDecoder)
            Decoder?.Dispose(); // цикл видео не стартовал — декодер освобождаем сами
        RaiseExited();
    }
}
