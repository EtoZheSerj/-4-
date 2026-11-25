using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Lab4Contrast;

public sealed class MainForm : Form
{
    private readonly PictureBox _sourceView = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(40, 40, 40)
    };

    private readonly PictureBox _resultView = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(40, 40, 40)
    };

    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Minimum = 0,
        Maximum = 100
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        Text = "Выберите изображение",
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private readonly NumericUpDown _poolSize;
    private readonly NumericUpDown _contrast;
    private readonly NumericUpDown _delay;

    private readonly Button _btnOpen = new() { Text = "Открыть...", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnRun = new() { Text = "Запустить обработку", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnCancel = new() { Text = "Отмена", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnSave = new() { Text = "Сохранить результат...", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Enabled = false };

    private string? _sourcePath;
    private Bitmap? _result;
    private ContrastProcessor? _processor;
    private bool _busy;

    public MainForm()
    {
        Text = "Лабораторная работа — контраст относительно средней яркости";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(820, 560);

        _poolSize = new NumericUpDown { Minimum = 1, Maximum = 64, Value = Math.Max(1, Environment.ProcessorCount), Width = 60 };
        _contrast = new NumericUpDown
        {
            Minimum = 0.1m,
            Maximum = 4.0m,
            DecimalPlaces = 1,
            Increment = 0.1m,
            Value = 1.5m,
            Width = 60
        };
        _delay = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 5, Value = 0, Width = 60 };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(6)
        };
        toolbar.Controls.AddRange(new Control[] { _btnOpen, _btnRun, _btnCancel, _btnSave });

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(6, 0, 6, 6)
        };
        settings.Controls.Add(new Label { Text = "Потоков в пуле:", AutoSize = true, Anchor = AnchorStyles.Left });
        settings.Controls.Add(_poolSize);
        settings.Controls.Add(new Label { Text = "Коэффициент контраста k:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(16, 3, 0, 0) });
        settings.Controls.Add(_contrast);
        settings.Controls.Add(new Label { Text = "Задержка на фрагмент, мс:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(16, 3, 0, 0) });
        settings.Controls.Add(_delay);

        var progressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(6, 0, 6, 6)
        };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        progressRow.Controls.Add(_status, 0, 0);
        progressRow.Controls.Add(_progress, 1, 0);

        var images = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        images.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        images.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        images.Controls.Add(new Label { Text = "Исходное изображение", AutoSize = true, Padding = new Padding(6, 4, 6, 2) }, 0, 0);
        images.Controls.Add(new Label { Text = "Результат", AutoSize = true, Padding = new Padding(6, 4, 6, 2) }, 1, 0);
        images.Controls.Add(_sourceView, 0, 1);
        images.Controls.Add(_resultView, 1, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(settings, 0, 1);
        root.Controls.Add(progressRow, 0, 2);
        root.Controls.Add(images, 0, 3);
        Controls.Add(root);

        _btnOpen.Click += OnOpen;
        _btnRun.Click += OnRun;
        _btnCancel.Click += OnCancel;
        _btnSave.Click += OnSave;
        FormClosing += OnFormClosing;
    }

    private void OnOpen(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы|*.*",
            Title = "Выбор входного изображения"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            using var loaded = Image.FromFile(dialog.FileName);
            var preview = new Bitmap(loaded);

            _sourcePath = dialog.FileName;
            _sourceView.Image?.Dispose();
            _sourceView.Image = preview;

            ClearResult();
            _status.Text = $"Выбрано: {Path.GetFileName(dialog.FileName)} ({loaded.Width}×{loaded.Height})";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private async void OnRun(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_sourcePath is null)
        {
            _status.Text = "Сначала выберите изображение";
            return;
        }

        int pool = (int)_poolSize.Value;
        double contrast = (double)_contrast.Value;
        int delay = (int)_delay.Value;

        _busy = true;
        SetBusyUi();
        ClearResult();
        _progress.Value = 0;
        _progress.Maximum = 100;

        var progress = new Progress<ProgressInfo>(p =>
        {
            if (_progress.Maximum != p.Total)
                _progress.Maximum = p.Total;

            int target = Math.Min(p.Done, _progress.Maximum);
            if (target > _progress.Value)
                _progress.Value = target;

            _status.Text = $"Обработка: {p.Done} / {p.Total} этапов";
        });

        var stopwatch = Stopwatch.StartNew();
        _processor = new ContrastProcessor(pool, pool, contrast, progress, delay);

        try
        {
            Bitmap? result = await _processor.ProcessAsync(_sourcePath);
            stopwatch.Stop();
            double mean = _processor.MeanBrightness;

            if (IsDisposed) return;

            if (result is null)
            {
                _status.Text = $"Отменено пользователем через {stopwatch.ElapsedMilliseconds} мс — результат не сохранён";
            }
            else
            {
                _result = result;
                _resultView.Image = result;
                _status.Text = $"Готово за {stopwatch.ElapsedMilliseconds} мс, M = {mean:F2}, пул {pool}";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка обработки: " + ex.Message;
        }
        finally
        {
            _processor?.Dispose();
            _processor = null;
            _busy = false;
            SetBusyUi();
        }
    }

    private void OnCancel(object? sender, EventArgs e)
    {
        if (!_busy || _processor is null)
        {
            _status.Text = "Отмена: активной обработки нет";
            return;
        }

        _processor.Cancel();
        _status.Text = "Отмена...";
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_result is null || _busy) return;

        using var dialog = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_contrast.png",
            Title = "Сохранение результата"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            await ContrastProcessor.SavePngAsync(_result, dialog.FileName);
            _status.Text = "Результат сохранён: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка сохранения: " + ex.Message;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _processor?.Cancel();
        ClearResult();
    }

    private void ClearResult()
    {
        _resultView.Image = null;
        _result?.Dispose();
        _result = null;
        _btnSave.Enabled = false;
    }
    private void SetBusyUi()
    {
        if (IsDisposed) return;
        _btnOpen.Enabled = !_busy;
        _btnRun.Enabled = !_busy;
        _btnCancel.Enabled = true;
        _btnSave.Enabled = !_busy && _result is not null;
        _poolSize.Enabled = !_busy;
        _contrast.Enabled = !_busy;
        _delay.Enabled = !_busy;
    }
}
