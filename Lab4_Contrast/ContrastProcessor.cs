using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lab4Contrast;

/// Прогресс: обработано этапов (фрагмент × этап) из общего числа.
public readonly record struct ProgressInfo(int Done, int Total);

public sealed class ContrastProcessor : IDisposable
{
    /// Сторона фрагмента (тайла) в пикселях.
    public const int TileSize = 64;

    /// Минимальный интервал между отчётами о прогрессе (чтобы не забивать UI-поток).
    private const int ProgressIntervalMs = 50;

    private readonly int _workerCount;
    private readonly int _maxParallel;
    private readonly double _contrast;
    private readonly int _delayPerTileMs;
    private readonly IProgress<ProgressInfo>? _progress;

    private readonly Semaphore _permits;
    private readonly AutoResetEvent[] _jobSignals;
    private readonly ManualResetEvent _cancel = new(false);
    private readonly ManualResetEvent _sumDone = new(false);
    private readonly ManualResetEvent _allDone = new(false);

    // Критическая секция: очереди этапов, общий буфер результата, накопитель яркости, счётчики.
    private readonly object _sync = new();
    private readonly Queue<Tile> _sumQueue = new();     // фаза 1: подсчёт яркости
    private readonly Queue<Tile> _applyQueue = new();   // фаза 2: применение контраста

    private Thread[] _workers = Array.Empty<Thread>();

    private byte[] _src = Array.Empty<byte>();   // исходные пиксели BGRA (после загрузки только читаются)
    private byte[] _dst = Array.Empty<byte>();   // общий буфер результата (пишется под lock)
    private int _stride;
    private int _width;
    private int _height;

    private int _tilesTotal;
    private int _passTotal;      // этапов: 2 на каждый фрагмент
    private int _passDone;       // счётчик прогресса
    private int _sumCount;       // сколько фрагментов прошло фазу 1
    private int _applyDone;      // сколько фрагментов прошло фазу 2
    private int _stage = 1;
    private bool _sumClosed;
    private bool _applyClosed;
    private bool _cancelled;
    private bool _disposed;

    private long _brightnessSum;     // Σ(299R + 587G + 114B) по всему кадру
    private long _pixelCount;
    private double _meanBrightness;
    private long _lastReportTick;    // троттлинг отчётов о прогрессе

    public ContrastProcessor(int workerCount, int maxParallel, double contrast, IProgress<ProgressInfo>? progress = null, int delayPerTileMs = 0)
    {
        if (workerCount < 1) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (maxParallel < 1) throw new ArgumentOutOfRangeException(nameof(maxParallel));
        if (contrast <= 0) throw new ArgumentOutOfRangeException(nameof(contrast));

        _workerCount = workerCount;
        _maxParallel = Math.Min(maxParallel, workerCount);
        _contrast = contrast;
        _delayPerTileMs = Math.Max(0, delayPerTileMs);
        _progress = progress;

        _permits = new Semaphore(_maxParallel, _maxParallel);
        _jobSignals = new AutoResetEvent[_workerCount];
        for (int i = 0; i < _workerCount; i++)
            _jobSignals[i] = new AutoResetEvent(false);
    }
    /// Средняя яркость кадра, вычисленная в фазе 1 (0..255)
    public double MeanBrightness
    {
        get { lock (_sync) return _meanBrightness; }
    }
    // ---------------------------------------------------------------------
    /// Полный проход: чтение → фаза 1 → фаза 2 → сборка. null, если обработка отменена.
    public async Task<Bitmap?> ProcessAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await LoadSourceAsync(path);

        // Отмена, нажатая во время чтения и декодирования файла, не должна теряться.
        if (_cancel.WaitOne(0))
            return null;

        ResetState();

        lock (_sync)
        {
            BuildTiles(_sumQueue);
            _tilesTotal = _sumQueue.Count;
            _passTotal = _tilesTotal * 2;
        }

        StartWorkers();

        // Фаза 1: заданий на подсчёт яркости больше не будет.
        lock (_sync)
        {
            _sumClosed = true;
        }
        WakeAll();

        // Ждём барьер «средняя яркость посчитана» либо «отмена» — без sleep-ов и опроса.
        int phase1 = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { _sumDone, _cancel }));
        if (phase1 != 0 || _cancel.WaitOne(0))
        {
            await StopWorkersAsync();
            return null;
        }

        double mean = ComputeMeanBrightness();

        // Фаза 2: публикуем среднюю яркость и ставим те же фрагменты на применение контраста.
        lock (_sync)
        {
            _meanBrightness = mean;
            _stage = 2;
            BuildTiles(_applyQueue);
            _applyClosed = true;
        }
        WakeAll();

        int phase2 = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { _allDone, _cancel }));
        await StopWorkersAsync();

        if (phase2 != 0 || _cancel.WaitOne(0))
            return null;   // частичный результат не собирается и не сохраняется

        return BuildBitmap();
    }

    public static async Task SavePngAsync(Bitmap bitmap, string path)
    {
        Bitmap copy = new Bitmap(bitmap);

        byte[] png = await Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream();
                copy.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                copy.Dispose();
            }
        });

        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await fs.WriteAsync(png);
        await fs.FlushAsync();
    }

    /// Запрос отмены: виден всем потокам пула и писателю.
    public void Cancel()
    {
        lock (_sync)
        {
            if (_cancelled) return;
            _cancelled = true;
        }
        _cancel.Set();
        WakeAll();   // разбудить тех, кто ждёт «появилось задание»
    }

    private async Task LoadSourceAsync(string path)
    {
        byte[] raw;

        await using (var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (fs.Length > int.MaxValue)
                throw new InvalidOperationException("Файл слишком велик.");

            raw = new byte[fs.Length];
            int offset = 0;
            while (offset < raw.Length)
            {
                int read = await fs.ReadAsync(raw.AsMemory(offset, raw.Length - offset));
                if (read == 0) break;
                offset += read;
            }
        }

        var prepared = await Task.Run(() => DecodeToBgra(raw));

        lock (_sync)
        {
            _src = prepared.src;
            _dst = prepared.dst;
            _width = prepared.width;
            _height = prepared.height;
            _stride = prepared.stride;
        }
    }

    private static (byte[] src, byte[] dst, int width, int height, int stride) DecodeToBgra(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        using var original = new Bitmap(stream);

        int width = original.Width;
        int height = original.Height;

        // Приводим к единому формату 32bppArgb, чтобы любой входной файл давал одинаковый буфер.
        using var normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(normalized))
        {
            g.DrawImage(original, 0, 0, width, height);
        }

        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var src = new byte[stride * height];
            Marshal.Copy(data.Scan0, src, 0, src.Length);
            return (src, new byte[src.Length], width, height, stride);
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    private void ResetState()
    {
        lock (_sync)
        {
            _sumQueue.Clear();
            _applyQueue.Clear();
            _tilesTotal = 0;
            _passTotal = 0;
            _passDone = 0;
            _sumCount = 0;
            _applyDone = 0;
            _stage = 1;
            _sumClosed = false;
            _applyClosed = false;
            _cancelled = false;
            _brightnessSum = 0;
            _pixelCount = 0;
            _meanBrightness = 0;
            _lastReportTick = 0;
        }

        _cancel.Reset();
        _sumDone.Reset();
        _allDone.Reset();
        foreach (var signal in _jobSignals)
            signal.Reset();
    }

    /// Планировщик: нарезка изображения на фрагменты. Вызывается под lock (_sync).
    private void BuildTiles(Queue<Tile> target)
    {
        for (int y = 0; y < _height; y += TileSize)
        {
            for (int x = 0; x < _width; x += TileSize)
            {
                int w = Math.Min(TileSize, _width - x);
                int h = Math.Min(TileSize, _height - y);
                target.Enqueue(new Tile(x, y, w, h));
            }
        }
    }

    private void StartWorkers()
    {
        _workers = new Thread[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            int index = i;
            _workers[i] = new Thread(() => WorkerLoop(index))
            {
                IsBackground = true,
                Name = $"worker-{index}"
            };
            _workers[i].Start();
        }
    }

    private Task StopWorkersAsync() => Task.Run(() =>
    {
        foreach (var worker in _workers)
            worker.Join();
        _workers = Array.Empty<Thread>();
    });

    private void WakeAll()
    {
        foreach (var signal in _jobSignals)
            signal.Set();
    }

    private void WorkerLoop(int index)
    {
        var handles = new WaitHandle[] { _cancel, _jobSignals[index] };

        while (true)
        {
            if (WaitHandle.WaitAny(handles) == 0)
                return;   // получен сигнал «отмена»

            // Внутренний цикл: поток пула забирает задания текущего этапа,
            // пока очередь этапа не опустеет.
            while (true)
            {
                Tile tile;
                int stage;
                lock (_sync)
                {
                    Queue<Tile> queue = _stage == 1 ? _sumQueue : _applyQueue;
                    bool closed = _stage == 1 ? _sumClosed : _applyClosed;

                    if (queue.Count == 0)
                    {
                        if (closed && _stage == 2)
                            return;      // все фрагменты обработаны — поток пула завершается
                        break;           // ждём сигнал «появилось задание» / начало фазы 2
                    }

                    tile = queue.Dequeue();
                    stage = _stage;
                }

                bool processed = stage == 1 ? RunSumStage(tile) : RunApplyStage(tile);
                if (!processed)
                    return;              // отмена при ожидании семафора
            }
        }
    }

    /// Фаза 1: сумма яркостных величин фрагмента. false — получена отмена.
    private bool RunSumStage(Tile tile)
    {
        if (!AcquirePermit())
            return false;

        try
        {
            if (_cancel.WaitOne(0))
                return false;

            if (!WaitDemoDelay())
                return false;

            (long sum, long count) = SumBrightness(tile);   // тяжёлая работа вне критической секции

            bool phaseFinished;
            int passDone;
            lock (_sync) // КРИТИЧЕСКАЯ СЕКЦИЯ: общий накопитель яркости + счётчики этапов
            {
                _brightnessSum += sum;
                _pixelCount += count;
                phaseFinished = ++_sumCount == _tilesTotal;
                passDone = ++_passDone;
            }

            ReportProgress(passDone);   // вне lock: UI-колбэк

            if (phaseFinished)
                _sumDone.Set();     // «средняя яркость посчитана» — барьер между фазами

            return true;
        }
        finally
        {
            _permits.Release();
        }
    }

    /// Фаза 2: применение контраста к фрагменту. false — получена отмена.
    private bool RunApplyStage(Tile tile)
    {
        if (!AcquirePermit())
            return false;

        try
        {
            if (_cancel.WaitOne(0))
                return false;

            if (!WaitDemoDelay())
                return false;

            byte[] processed = ProcessTile(tile, MeanBrightness);   // вне критической секции

            int passDone;
            int applyDone;
            lock (_sync) // КРИТИЧЕСКАЯ СЕКЦИЯ: общий буфер результата + счётчики прогресса
            {
                CopyTileToResult(processed, tile);
                passDone = ++_passDone;
                applyDone = ++_applyDone;
            }

            ReportProgress(passDone);

            if (applyDone == _tilesTotal)
                _allDone.Set();     // «все фрагменты обработаны»

            return true;
        }
        finally
        {
            _permits.Release();
        }
    }

    /// Демонстрационная пауза на фрагмент: прерывается сигналом отмены, а не Thread.Sleep.
    private bool WaitDemoDelay()
        => _delayPerTileMs <= 0 || !_cancel.WaitOne(_delayPerTileMs);

    private void ReportProgress(int done)
    {
        if (done < _passTotal)
        {
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref _lastReportTick);
            if (now - previous < ProgressIntervalMs)
                return;
            if (Interlocked.CompareExchange(ref _lastReportTick, now, previous) != previous)
                return;
        }

        _progress?.Report(new ProgressInfo(done, _passTotal));
    }

    private bool AcquirePermit()
    {
        // Семафор ограничивает число одновременно обрабатываемых фрагментов.
        while (!_permits.WaitOne(50))
        {
            if (_cancel.WaitOne(0))
                return false;
        }
        return true;
    }

    /// Сумма яркостных величин 299R+587G+114B по фрагменту (вне критической секции).
    private (long sum, long count) SumBrightness(Tile tile)
    {
        long sum = 0;
        int rowBytes = tile.W * 4;

        for (int r = 0; r < tile.H; r++)
        {
            int rowStart = (tile.Y + r) * _stride + tile.X * 4;
            for (int i = 0; i < rowBytes; i += 4)
            {
                int b = _src[rowStart + i];
                int g = _src[rowStart + i + 1];
                int red = _src[rowStart + i + 2];
                sum += 299 * red + 587 * g + 114 * b;
            }
        }

        return (sum, (long)tile.W * tile.H);
    }

    private double ComputeMeanBrightness()
    {
        lock (_sync)
        {
            return _pixelCount == 0 ? 0 : (double)_brightnessSum / (1000.0 * _pixelCount);
        }
    }

    /// Обработка одного фрагмента в приватном буфере: данные других потоков не затрагиваются.
    private byte[] ProcessTile(Tile tile, double mean)
    {
        int rowBytes = tile.W * 4;
        var local = new byte[rowBytes * tile.H];

        // _src после загрузки неизменяем, поэтому чтение без блокировки безопасно.
        for (int r = 0; r < tile.H; r++)
        {
            Buffer.BlockCopy(_src, (tile.Y + r) * _stride + tile.X * 4, local, r * rowBytes, rowBytes);
        }

        ApplyContrast(local, mean, _contrast);
        return local;
    }

    /// Копирование готового фрагмента в общий буфер. Только под lock (_sync).
    private void CopyTileToResult(byte[] local, Tile tile)
    {
        int rowBytes = tile.W * 4;
        for (int r = 0; r < tile.H; r++)
        {
            Buffer.BlockCopy(local, r * rowBytes, _dst, (tile.Y + r) * _stride + tile.X * 4, rowBytes);
        }
    }

    public static void ApplyContrast(byte[] buffer, double mean, double k)
    {
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            buffer[i] = Adjust(buffer[i], mean, k);
            buffer[i + 1] = Adjust(buffer[i + 1], mean, k);
            buffer[i + 2] = Adjust(buffer[i + 2], mean, k);
            // buffer[i + 3] (альфа) не меняется
        }
    }

    private static byte Adjust(byte channel, double mean, double k)
    {
        double value = mean + k * (channel - mean);
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)(value + 0.5);
    }

    private Bitmap BuildBitmap()
    {
        Bitmap result;
        lock (_sync)
        {
            result = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, _width, _height);
            BitmapData data = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == _stride && _dst.Length == _stride * _height)
                {
                    Marshal.Copy(_dst, 0, data.Scan0, _dst.Length);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        Marshal.Copy(_dst, y * _stride, data.Scan0 + y * data.Stride, Math.Min(_stride, data.Stride));
                    }
                }
            }
            finally
            {
                result.UnlockBits(data);
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;

        Cancel();
        foreach (var worker in _workers)
        {
            if (worker.IsAlive)
                worker.Join(5000);
        }
        _workers = Array.Empty<Thread>();

        _disposed = true;

        _permits.Dispose();
        foreach (var signal in _jobSignals)
            signal.Dispose();
        _cancel.Dispose();
        _sumDone.Dispose();
        _allDone.Dispose();
    }

    private readonly record struct Tile(int X, int Y, int W, int H);
}
