using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DtsMonitor.App.Models;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace DtsMonitor.App;

public partial class CalibrationWindow : Window
{
    private const float CalibrationPositionScaleToMeters = 0.1f;
    private static readonly TimeSpan CurrentRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CalibrationWaveRefreshInterval = TimeSpan.FromSeconds(5);
    private const int MaxRenderedSamplesPerWave = 12000;
    private readonly Func<int, CalibrationWindowParameters> _getParameters;
    private readonly Func<int, float, int, Task<string?>> _startCalibration;
    private readonly Func<int, Task<string?>> _stopCalibration;
    private readonly Func<int, int, Task<string?>> _setCalibrationCurrent;
    private readonly Func<int, float, Task<string?>> _recalculateCalibrationPositions;
    private readonly Func<int, IReadOnlyList<CalibrationRowItem>, string?> _saveCoefficient;
    private readonly Func<int, CalibrationResultModel?> _getCalibrationResult;
    private readonly Func<int, CalibrationWaveDataModel?> _getCalibrationWaveData;
    private readonly Func<int> _getCurrentAmplifierCurrent;
    private readonly Func<int, IReadOnlyList<CalibrationRowItem>?> _getEditedCalibrationRows;
    private readonly Func<int, IReadOnlyList<CalibrationRowItem>?> _getFallbackCalibrationRows;
    private readonly Action<int, IReadOnlyList<CalibrationRowItem>> _persistEditedCalibrationRows;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _chartRenderTimer;
    private CalibrationChartPanel _chartPanel = null!;
    private readonly ObservableCollection<CalibrationRowItem> _calibrationRows = new();
    private readonly List<int> _availableChannelIndexes = new();
    private readonly Dictionary<int, HashSet<int>> _deletedSourceIndexesByChannel = new();
    private readonly Dictionary<int, int> _selectedRowSourceIndexByChannel = new();
    private readonly Brush[] _waveBrushes =
    {
        BrushFromHex("#FF4A4A"),
        BrushFromHex("#2F72FF"),
        BrushFromHex("#FFB347"),
        BrushFromHex("#3ED6B6"),
        BrushFromHex("#D28BFF"),
        BrushFromHex("#FF7EA8"),
        BrushFromHex("#FFE169")
    };

    private CalibrationResultModel? _latestCalibrationResult;
    private CalibrationWaveDataModel? _latestCalibrationWaveData;
    private DateTime _lastCurrentRefreshUtc = DateTime.MinValue;
    private DateTime _lastWaveRefreshUtc = DateTime.MinValue;
    private int _latestAmplifierCurrent;
    private long _lastRenderedWaveTimestampMs = -1;
    private int _lastRenderedChannel = -1;
    private int _lastRenderedWaveCount = -1;
    private int _lastRenderedSampleCount = -1;
    private double _lastRenderedWidth = -1;
    private double _lastRenderedHeight = -1;
    private bool _chartRenderInvalidated = true;
    private long _cachedBoundsWaveTimestampMs = -1;
    private int _cachedBoundsChannel = -1;
    private int _cachedBoundsWaveCount = -1;
    private int _cachedBoundsSampleCount = -1;
    private float _cachedBoundsXMin;
    private float _cachedBoundsXMax;
    private float _cachedBoundsYMin;
    private float _cachedBoundsYMax;
    private bool _isCalibrating;
    private bool _isInitializing;
    private bool _isContextMenuOpen;
    private bool _isClosingAfterStop;
    private bool _calibrationRowsRefreshPending = true;
    private int _selectedChannelIndex;
    private int _fiberLengthM = 360;
    private float _startLengthM;
    private float[] _centerWavelengths = Array.Empty<float>();
    private bool _multiWaveReverse;
    private float? _chartZoomXMin;
    private float? _chartZoomXMax;
    private float? _chartZoomYMin;
    private float? _chartZoomYMax;
    private bool _isChartPanning;
    private Point _chartPanStartPoint;
    private float _chartPanStartXMin;
    private float _chartPanStartXMax;
    private float _chartPanStartYMin;
    private float _chartPanStartYMax;

    public CalibrationWindow(
        Func<int, CalibrationWindowParameters> getParameters,
        Func<int, float, int, Task<string?>> startCalibration,
        Func<int, Task<string?>> stopCalibration,
        Func<int, int, Task<string?>> setCalibrationCurrent,
        Func<int, float, Task<string?>> recalculateCalibrationPositions,
        Func<int, IReadOnlyList<CalibrationRowItem>, string?> saveCoefficient,
        Func<int, CalibrationResultModel?> getCalibrationResult,
        Func<int, CalibrationWaveDataModel?> getCalibrationWaveData,
        Func<int> getCurrentAmplifierCurrent,
        Func<int, IReadOnlyList<CalibrationRowItem>?> getEditedCalibrationRows,
        Func<int, IReadOnlyList<CalibrationRowItem>?> getFallbackCalibrationRows,
        Action<int, IReadOnlyList<CalibrationRowItem>> persistEditedCalibrationRows)
    {
        _getParameters = getParameters;
        _startCalibration = startCalibration;
        _stopCalibration = stopCalibration;
        _setCalibrationCurrent = setCalibrationCurrent;
        _recalculateCalibrationPositions = recalculateCalibrationPositions;
        _saveCoefficient = saveCoefficient;
        _getCalibrationResult = getCalibrationResult;
        _getCalibrationWaveData = getCalibrationWaveData;
        _getCurrentAmplifierCurrent = getCurrentAmplifierCurrent;
        _getEditedCalibrationRows = getEditedCalibrationRows;
        _getFallbackCalibrationRows = getFallbackCalibrationRows;
        _persistEditedCalibrationRows = persistEditedCalibrationRows;
        CalibrationRows = _calibrationRows;

        InitializeComponent();
        DataContext = this;
        _chartPanel = new CalibrationChartPanel(this);
        CalibrationChartHost.Child = _chartPanel;
        _chartPanel.MouseWheel += CalibrationChartPanel_MouseWheel;
        _chartPanel.MouseDown += CalibrationChartPanel_MouseDown;
        _chartPanel.MouseMove += CalibrationChartPanel_MouseMove;
        _chartPanel.MouseUp += CalibrationChartPanel_MouseUp;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += (_, _) =>
        {
            if (_isCalibrating && !_isContextMenuOpen)
            {
                RefreshCalibrationData();
            }
        };
        _chartRenderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _chartRenderTimer.Tick += (_, _) =>
        {
            _chartRenderTimer.Stop();
            RenderCalibrationChart();
        };

        UpdateParameters(_getParameters(0));
        Loaded += CalibrationWindow_Loaded;
        Closing += CalibrationWindow_Closing;
        Closed += CalibrationWindow_Closed;
    }

    public ObservableCollection<CalibrationRowItem> CalibrationRows { get; }

    public void UpdateParameters(CalibrationWindowParameters parameters)
    {
        _isInitializing = true;
        try
        {
            UpdateAvailableChannels(parameters.AvailableChannelIndexes, parameters.ChannelIndex);
            _selectedChannelIndex = ResolveAvailableChannel(parameters.ChannelIndex);
            _fiberLengthM = Math.Max(1, parameters.FiberLengthM);
            _startLengthM = Math.Max(0.0f, parameters.StartLengthM);
            _centerWavelengths = parameters.CenterWavelengths?.ToArray() ?? Array.Empty<float>();
            _multiWaveReverse = parameters.MultiWaveReverse;
            ChannelComboBox.SelectedIndex = _availableChannelIndexes.IndexOf(_selectedChannelIndex);
            ThresholdTextBox.Text = parameters.Threshold.ToString("0.00", CultureInfo.InvariantCulture);
            CalibrationCurrentTextBox.Text = parameters.CalibrationCurrentMa.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isInitializing = false;
        }

        if (IsLoaded)
        {
            _calibrationRowsRefreshPending = true;
            RefreshCalibrationData();
        }
    }

    public void NotifyCalibrationStopped()
    {
        _isCalibrating = false;
        InvalidateCalibrationRefresh(forceWaveRefresh: true, forceRowsRefresh: true);
        StatusTextBlock.Text = "校准已停止。";
        RefreshCalibrationData();
    }

    private void CalibrationWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveWindowLayout();
        _pollTimer.Start();
        StatusTextBlock.Text = "等待开始校准";
        RefreshCalibrationData();
    }

    private void ApplyAdaptiveWindowLayout()
    {
        WindowLayoutHelper.FitAndCenter(this, 1580, 920, allowUpscale: true, maxScale: 1.6);
    }

    private void CalibrationWindow_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _chartRenderTimer.Stop();
    }

    private async void CalibrationWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterStop || !_isCalibrating)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        StatusTextBlock.Text = "正在停止校准...";

        string? error = await _stopCalibration(_selectedChannelIndex);
        if (!string.IsNullOrWhiteSpace(error))
        {
            IsEnabled = true;
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        _isCalibrating = false;
        InvalidateCalibrationRefresh(forceWaveRefresh: true);
        _isClosingAfterStop = true;
        Close();
    }

    private void ChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ChannelComboBox.SelectedIndex < 0 || ChannelComboBox.SelectedIndex >= _availableChannelIndexes.Count)
        {
            return;
        }

        _selectedChannelIndex = _availableChannelIndexes[ChannelComboBox.SelectedIndex];
        CalibrationWindowParameters parameters = _getParameters(_selectedChannelIndex);
        _isInitializing = true;
        try
        {
            UpdateAvailableChannels(parameters.AvailableChannelIndexes, _selectedChannelIndex);
            ChannelComboBox.SelectedIndex = _availableChannelIndexes.IndexOf(_selectedChannelIndex);
        }
        finally
        {
            _isInitializing = false;
        }
        _fiberLengthM = Math.Max(1, parameters.FiberLengthM);
        _startLengthM = Math.Max(0.0f, parameters.StartLengthM);
        _centerWavelengths = parameters.CenterWavelengths?.ToArray() ?? Array.Empty<float>();
        _multiWaveReverse = parameters.MultiWaveReverse;
        ThresholdTextBox.Text = parameters.Threshold.ToString("0.00", CultureInfo.InvariantCulture);
        CalibrationCurrentTextBox.Text = parameters.CalibrationCurrentMa.ToString(CultureInfo.InvariantCulture);
        InvalidateCalibrationRefresh(forceWaveRefresh: true, forceRowsRefresh: true);
        RefreshCalibrationData();
    }

    private async void StartCalibrationInnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUiFloat(ThresholdTextBox.Text, out float threshold) || threshold <= 0)
        {
            AppMessageDialog.ShowInfo(this, "校准", "校准阈值无效。");
            return;
        }

        if (!int.TryParse(CalibrationCurrentTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int current) ||
            current < 0)
        {
            AppMessageDialog.ShowInfo(this, "校准", "设置电流无效。");
            return;
        }

        string? error = await _startCalibration(_selectedChannelIndex, threshold, current);
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        _deletedSourceIndexesByChannel.Remove(_selectedChannelIndex);
        _isCalibrating = true;
        InvalidateCalibrationRefresh(forceWaveRefresh: true);
        StatusTextBlock.Text = $"CH{_selectedChannelIndex + 1} 校准中，阈值={threshold:0.00}，设置电流={current} mA";
        RefreshCalibrationData();
    }

    private async void StopCalibrationInnerButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = await _stopCalibration(_selectedChannelIndex);
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        _isCalibrating = false;
        InvalidateCalibrationRefresh(forceWaveRefresh: true, forceRowsRefresh: true);
        StatusTextBlock.Text = "校准已停止。";
        RefreshCalibrationData();
    }

    private async void RecalculatePositionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUiFloat(ThresholdTextBox.Text, out float threshold) || threshold <= 0)
        {
            AppMessageDialog.ShowInfo(this, "校准", "校准阈值无效。");
            return;
        }

        string? error = await _recalculateCalibrationPositions(_selectedChannelIndex, threshold);
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        StatusTextBlock.Text = $"CH{_selectedChannelIndex + 1} 已按阈值 {threshold:0.00} 重新计算光栅位置。";
        _deletedSourceIndexesByChannel.Remove(_selectedChannelIndex);
        _selectedRowSourceIndexByChannel.Remove(_selectedChannelIndex);
        InvalidateCalibrationRefresh(forceWaveRefresh: false, forceRowsRefresh: true);
        RefreshCalibrationData();
    }

    private async void SetCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CalibrationCurrentTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int current) ||
            current < 0)
        {
            AppMessageDialog.ShowInfo(this, "校准", "设置电流无效。");
            return;
        }

        string? error = await _setCalibrationCurrent(_selectedChannelIndex, current);
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        StatusTextBlock.Text = $"CH{_selectedChannelIndex + 1} 已设置校准电流为 {current} mA。";
        RefreshCalibrationData();
    }

    private void SaveCoefficientButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = _saveCoefficient(_selectedChannelIndex, _calibrationRows.ToList());
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "保存系数", error);
            return;
        }

        StatusTextBlock.Text = $"CH{_selectedChannelIndex + 1} 系数文件已保存。";
    }

    private void CalibrationChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CalibrationChartHost.Width = e.NewSize.Width;
        CalibrationChartHost.Height = e.NewSize.Height;
        RequestCalibrationChartRender();
    }

    private void CalibrationChartPanel_MouseWheel(object? sender, WF.MouseEventArgs e)
    {
        HandleChartWheel(ToCalibrationChartCanvasPoint(e), e.Delta, (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
    }

    private void CalibrationChartPanel_MouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Right)
        {
            ResetCalibrationChartZoom();
            return;
        }

        if (e.Button == WF.MouseButtons.Left)
        {
            BeginChartPan(ToCalibrationChartCanvasPoint(e), captureWpfCanvas: false);
        }
    }

    private void CalibrationChartPanel_MouseMove(object? sender, WF.MouseEventArgs e)
    {
        ContinueChartPan(ToCalibrationChartCanvasPoint(e));
    }

    private void CalibrationChartPanel_MouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left)
        {
            EndCalibrationChartPan();
        }
    }

    private Point ToCalibrationChartCanvasPoint(WF.MouseEventArgs e)
    {
        double canvasWidth = Math.Max(1.0, CalibrationChartCanvas.ActualWidth);
        double canvasHeight = Math.Max(1.0, CalibrationChartCanvas.ActualHeight);
        double panelWidth = Math.Max(1.0, _chartPanel.Width);
        double panelHeight = Math.Max(1.0, _chartPanel.Height);
        return new Point(e.X * canvasWidth / panelWidth, e.Y * canvasHeight / panelHeight);
    }

    private void CalibrationChartCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        HandleChartWheel(e.GetPosition(CalibrationChartCanvas), e.Delta, (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
        e.Handled = true;
    }

    private void HandleChartWheel(Point position, int delta, bool zoomYOnly)
    {
        if (!TryGetCalibrationChartBounds(out Rect plotRect, out float fullXMin, out float fullXMax, out float fullYMin, out float fullYMax))
        {
            return;
        }

        if (!plotRect.Contains(position))
        {
            return;
        }

        EnsureCalibrationChartZoomInitialized(fullXMin, fullXMax, fullYMin, fullYMax);

        float factor = delta > 0 ? 0.84f : 1.18f;

        if (zoomYOnly)
        {
            float center = fullYMax - (float)((position.Y - plotRect.Top) / plotRect.Height) * (fullYMax - fullYMin);
            ApplyZoom(
                ref _chartZoomYMin,
                ref _chartZoomYMax,
                fullYMin,
                fullYMax,
                center,
                factor,
                Math.Max((fullYMax - fullYMin) * 0.01f, 0.01f));
        }
        else
        {
            float center = fullXMin + (float)((position.X - plotRect.Left) / plotRect.Width) * (fullXMax - fullXMin);
            ApplyZoom(
                ref _chartZoomXMin,
                ref _chartZoomXMax,
                fullXMin,
                fullXMax,
                center,
                factor,
                Math.Max((fullXMax - fullXMin) * 0.01f, 0.5f));
        }

        _chartRenderInvalidated = true;
        RequestCalibrationChartRender();
    }

    private void CalibrationChartCanvas_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ResetCalibrationChartZoom();
        e.Handled = true;
    }

    private void CalibrationChartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginChartPan(e.GetPosition(CalibrationChartCanvas));
        e.Handled = _isChartPanning;
    }

    private void BeginChartPan(Point position, bool captureWpfCanvas = true)
    {
        if (!TryGetCalibrationChartBounds(out Rect plotRect, out float fullXMin, out float fullXMax, out float fullYMin, out float fullYMax))
        {
            return;
        }

        if (!plotRect.Contains(position))
        {
            return;
        }

        EnsureCalibrationChartZoomInitialized(fullXMin, fullXMax, fullYMin, fullYMax);
        _isChartPanning = true;
        _chartPanStartPoint = position;
        _chartPanStartXMin = _chartZoomXMin!.Value;
        _chartPanStartXMax = _chartZoomXMax!.Value;
        _chartPanStartYMin = _chartZoomYMin!.Value;
        _chartPanStartYMax = _chartZoomYMax!.Value;
        if (captureWpfCanvas)
        {
            CalibrationChartCanvas.CaptureMouse();
        }
        _chartPanel.Capture = true;
    }

    private void CalibrationChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        ContinueChartPan(e.GetPosition(CalibrationChartCanvas));
        e.Handled = _isChartPanning;
    }

    private void ContinueChartPan(Point position)
    {
        if (!_isChartPanning)
        {
            return;
        }

        if (!TryGetCalibrationChartBounds(out Rect plotRect, out float fullXMin, out float fullXMax, out float fullYMin, out float fullYMax))
        {
            EndCalibrationChartPan();
            return;
        }

        double dx = position.X - _chartPanStartPoint.X;
        double dy = position.Y - _chartPanStartPoint.Y;
        float xSpan = Math.Max(0.0001f, _chartPanStartXMax - _chartPanStartXMin);
        float ySpan = Math.Max(0.0001f, _chartPanStartYMax - _chartPanStartYMin);
        float xDelta = (float)(dx / Math.Max(1.0, plotRect.Width) * xSpan);
        float yDelta = (float)(dy / Math.Max(1.0, plotRect.Height) * ySpan);

        float nextXMin = _chartPanStartXMin - xDelta;
        float nextXMax = _chartPanStartXMax - xDelta;
        ClampPanRange(ref nextXMin, ref nextXMax, fullXMin, fullXMax);

        float nextYMin = _chartPanStartYMin + yDelta;
        float nextYMax = _chartPanStartYMax + yDelta;
        ClampPanRange(ref nextYMin, ref nextYMax, fullYMin, fullYMax);

        _chartZoomXMin = nextXMin;
        _chartZoomXMax = nextXMax;
        _chartZoomYMin = nextYMin;
        _chartZoomYMax = nextYMax;

        RequestCalibrationChartRender();
    }

    private void CalibrationChartCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isChartPanning)
        {
            return;
        }

        EndCalibrationChartPan();
        e.Handled = true;
    }

    private void CalibrationChartCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isChartPanning || CalibrationChartCanvas.IsMouseCaptured)
        {
            return;
        }

        EndCalibrationChartPan();
    }

    private void EndCalibrationChartPan()
    {
        _isChartPanning = false;
        if (CalibrationChartCanvas.IsMouseCaptured)
        {
            CalibrationChartCanvas.ReleaseMouseCapture();
        }
        _chartPanel.Capture = false;
    }

    private void RefreshCalibrationData()
    {
        DateTime nowUtc = DateTime.UtcNow;

        if (nowUtc - _lastCurrentRefreshUtc >= CurrentRefreshInterval)
        {
            _latestAmplifierCurrent = _getCurrentAmplifierCurrent();
            _lastCurrentRefreshUtc = nowUtc;
        }
        CurrentValueTextBlock.Text = _latestAmplifierCurrent.ToString(CultureInfo.InvariantCulture);

        bool rowsChanged = false;
        if (_calibrationRowsRefreshPending)
        {
            _calibrationRowsRefreshPending = false;
            rowsChanged = RefreshCalibrationRows();
        }

        bool waveChanged = false;
        if (nowUtc - _lastWaveRefreshUtc >= CalibrationWaveRefreshInterval)
        {
            _lastWaveRefreshUtc = nowUtc;
            CalibrationWaveDataModel? waveData = _getCalibrationWaveData(_selectedChannelIndex);
            if (waveData is not null)
            {
                waveChanged = _latestCalibrationWaveData is null ||
                    _latestCalibrationWaveData.TimestampMs != waveData.TimestampMs ||
                    _latestCalibrationWaveData.Channel != waveData.Channel ||
                    _latestCalibrationWaveData.WaveCount != waveData.WaveCount ||
                    _latestCalibrationWaveData.SampleCount != waveData.SampleCount;
                if (waveChanged)
                {
                    _cachedBoundsWaveTimestampMs = -1;
                }
                _latestCalibrationWaveData = waveData;
            }
        }

        if (rowsChanged || waveChanged || _chartRenderInvalidated)
        {
            RenderCalibrationChart();
        }
    }

    private bool RefreshCalibrationRows()
    {
        CalibrationResultModel? result = _getCalibrationResult(_selectedChannelIndex);
        IReadOnlyList<CalibrationRowItem>? editedRows = _getEditedCalibrationRows(_selectedChannelIndex);
        if (editedRows is not null)
        {
            ApplyCalibrationRows(editedRows);
            return true;
        }

        if (result is not null)
        {
            _latestCalibrationResult = result;
            UpdateCalibrationRows(result);
            return true;
        }

        if (_getFallbackCalibrationRows(_selectedChannelIndex) is IReadOnlyList<CalibrationRowItem> fallbackRows)
        {
            ApplyCalibrationRows(fallbackRows);
            return true;
        }

        if (_calibrationRows.Count > 0)
        {
            _calibrationRows.Clear();
            return true;
        }

        return false;
    }

    private void InvalidateCalibrationRefresh(bool forceWaveRefresh, bool forceRowsRefresh = false)
    {
        if (forceRowsRefresh)
        {
            _calibrationRowsRefreshPending = true;
        }

        if (forceWaveRefresh)
        {
            _lastWaveRefreshUtc = DateTime.MinValue;
            _latestCalibrationWaveData = null;
            _cachedBoundsWaveTimestampMs = -1;
        }
        _chartRenderInvalidated = true;
    }

    private void UpdateCalibrationRows(CalibrationResultModel result)
    {
        _calibrationRows.Clear();
        float positionScaleToMeters = GetCurrentPositionScaleToMeters();
        HashSet<int>? deletedSourceIndexes = _deletedSourceIndexesByChannel.TryGetValue(_selectedChannelIndex, out HashSet<int>? deleted)
            ? deleted
            : null;
        var rows = new List<CalibrationRowItem>(result.SensorPositionsRaw.Length);
        int[] rawWaveIndexes = result.SensorWaveIndexesRaw;
        bool rawWaveIndexesAreOneBased = AreWaveIndexesOneBased(rawWaveIndexes);
        for (int i = 0; i < result.SensorPositionsRaw.Length; i++)
        {
            if (deletedSourceIndexes is not null && deletedSourceIndexes.Contains(i))
            {
                continue;
            }

            int rawSamplePoint = result.SensorPositionsRaw[i];
            float sensorPositionM = rawSamplePoint * positionScaleToMeters;
            int rawWaveIndex = i < rawWaveIndexes.Length ? rawWaveIndexes[i] : 0;
            int waveIndex = InferDisplayWaveIndexFromWaveData(rawSamplePoint) ??
                            NormalizeWaveIndexForDisplay(rawWaveIndex, rawWaveIndexesAreOneBased);

            rows.Add(new CalibrationRowItem
            {
                SourceIndex = i,
                RelativeSamplePoint = rawSamplePoint,
                SamplePoint = rawSamplePoint,
                PositionM = sensorPositionM,
                WaveIndex = waveIndex
            });
        }

        int displayIndex = 1;
        foreach (CalibrationRowItem row in rows
                     .OrderBy(x => x.SamplePoint)
                     .ThenBy(x => x.WaveIndex)
                     .ThenBy(x => x.SourceIndex))
        {
            row.Index = displayIndex++;
            _calibrationRows.Add(row);
        }
    }

    private void RenderCalibrationChart()
    {
        double width = CalibrationChartCanvas.ActualWidth;
        double height = CalibrationChartCanvas.ActualHeight;
        if (width < 160 || height < 120)
        {
            return;
        }

        if (!_chartRenderInvalidated &&
            _lastRenderedWaveTimestampMs == (_latestCalibrationWaveData?.TimestampMs ?? -1) &&
            _lastRenderedChannel == (_latestCalibrationWaveData?.Channel ?? -1) &&
            _lastRenderedWaveCount == (_latestCalibrationWaveData?.WaveCount ?? -1) &&
            _lastRenderedSampleCount == (_latestCalibrationWaveData?.SampleCount ?? -1) &&
            Math.Abs(_lastRenderedWidth - width) < 0.5 &&
            Math.Abs(_lastRenderedHeight - height) < 0.5)
        {
            return;
        }

        CalibrationChartCanvas.Children.Clear();

        if (_latestCalibrationWaveData is null ||
            _latestCalibrationWaveData.Channel != _selectedChannelIndex ||
            _latestCalibrationWaveData.WaveCount <= 0 ||
            _latestCalibrationWaveData.SampleCount <= 0 ||
            _latestCalibrationWaveData.WaveValues.Length == 0)
        {
            SetChartPlaceholder(width, height, _isCalibrating ? "等待校准波形数据..." : "点击开始进入校准。");
            return;
        }

        int waveCount = _latestCalibrationWaveData.WaveCount;
        int sampleCount = _latestCalibrationWaveData.SampleCount;
        float[] values = _latestCalibrationWaveData.WaveValues;
        int expected = waveCount * sampleCount;
        if (values.Length < expected)
        {
            SetChartPlaceholder(width, height, "校准波形数据长度不足。");
            return;
        }

        const double left = 72;
        const double top = 34;
        const double right = 30;
        const double bottom = 58;

        var plotRect = new Rect(left, top, Math.Max(10, width - left - right), Math.Max(10, height - top - bottom));
        var clip = new RectangleGeometry(plotRect);

        float positionScaleToMeters = GetPositionScaleToMeters(sampleCount);
        float xMin;
        float xMax;
        float yMin;
        float yMax;
        bool hasCachedBounds = TryGetCalibrationDataBounds(out xMin, out xMax, out yMin, out yMax);

        float scannedYMin = float.MaxValue;
        float scannedYMax = float.MinValue;
        for (int i = 0; !hasCachedBounds && i < expected; i++)
        {
            float value = values[i];
            if (!float.IsFinite(value))
            {
                continue;
            }

            scannedYMin = Math.Min(scannedYMin, value);
            scannedYMax = Math.Max(scannedYMax, value);
        }

        if (!hasCachedBounds && (!float.IsFinite(scannedYMin) || !float.IsFinite(scannedYMax) || scannedYMin == float.MaxValue || scannedYMax == float.MinValue))
        {
            SetChartPlaceholder(width, height, "校准波形数据无效。");
            return;
        }

        if (!hasCachedBounds)
        {
            xMin = 0.0f;
            xMax = Math.Max(positionScaleToMeters, Math.Max(1, sampleCount - 1) * positionScaleToMeters);
            yMin = scannedYMin;
            yMax = scannedYMax;

            if (Math.Abs(yMax - yMin) < 1e-6f)
            {
                yMin -= 1.0f;
                yMax += 1.0f;
            }
            else
            {
                float padding = (yMax - yMin) * 0.08f;
                yMin -= padding;
                yMax += padding;
            }
        }

        ApplyCalibrationChartZoom(ref xMin, ref xMax, ref yMin, ref yMax);

        CalibrationChartCanvas.Children.Clear();
        CalibrationChartHost.Width = width;
        CalibrationChartHost.Height = height;
        CalibrationChartCanvas.Children.Add(CalibrationChartHost);
        _chartPanel.SetChart(
            values,
            waveCount,
            sampleCount,
            positionScaleToMeters,
            xMin,
            xMax,
            yMin,
            yMax);
        MarkCalibrationChartRendered(width, height);
        return;
    }

    private void MarkCalibrationChartRendered(double width, double height)
    {
        _lastRenderedWaveTimestampMs = _latestCalibrationWaveData?.TimestampMs ?? -1;
        _lastRenderedChannel = _latestCalibrationWaveData?.Channel ?? -1;
        _lastRenderedWaveCount = _latestCalibrationWaveData?.WaveCount ?? -1;
        _lastRenderedSampleCount = _latestCalibrationWaveData?.SampleCount ?? -1;
        _lastRenderedWidth = width;
        _lastRenderedHeight = height;
        _chartRenderInvalidated = false;
    }

    private void DrawGrid(Rect plotRect, float xMin, float xMax, float yMin, float yMax)
    {
        var border = new Rectangle
        {
            Width = plotRect.Width,
            Height = plotRect.Height,
            Stroke = BrushFromHex("#8A8A8A"),
            StrokeThickness = 1
        };
        Canvas.SetLeft(border, plotRect.Left);
        Canvas.SetTop(border, plotRect.Top);
        CalibrationChartCanvas.Children.Add(border);

        for (int i = 0; i <= 4; i++)
        {
            double y = plotRect.Top + plotRect.Height * i / 4.0;
            var line = new Line
            {
                X1 = plotRect.Left,
                X2 = plotRect.Right,
                Y1 = y,
                Y2 = y,
                Stroke = BrushFromHex("#666666"),
                StrokeThickness = 1
            };
            CalibrationChartCanvas.Children.Add(line);

            float value = yMax - (float)(i / 4.0) * (yMax - yMin);
            AddAxisLabel(plotRect.Left - 18, y - 10, value.ToString("0.00", CultureInfo.InvariantCulture), HorizontalAlignment.Right);
        }

        for (int i = 0; i <= 4; i++)
        {
            double x = plotRect.Left + plotRect.Width * i / 4.0;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = plotRect.Top,
                Y2 = plotRect.Bottom,
                Stroke = BrushFromHex("#666666"),
                StrokeThickness = 1
            };
            CalibrationChartCanvas.Children.Add(line);

            float value = xMin + (float)(i / 4.0) * (xMax - xMin);
            AddAxisLabel(x, plotRect.Bottom + 8, value.ToString("0.0", CultureInfo.InvariantCulture), HorizontalAlignment.Center);
        }

        AddAxisTitle(plotRect.Left + plotRect.Width / 2.0, plotRect.Bottom + 28, "距离(m)");
    }
    private void AddAxisLabel(double x, double y, string text, HorizontalAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 12
        };

        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = alignment switch
        {
            HorizontalAlignment.Center => x - label.DesiredSize.Width / 2.0,
            HorizontalAlignment.Right => x - label.DesiredSize.Width,
            _ => x
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, y);
        CalibrationChartCanvas.Children.Add(label);
    }

    private void AddAxisTitle(double x, double y, string text)
    {
        var title = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 13
        };
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(title, x - title.DesiredSize.Width / 2.0);
        Canvas.SetTop(title, y);
        CalibrationChartCanvas.Children.Add(title);
    }

    private void AddLegendItem(int waveIndex, int waveCount, Brush stroke)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 0)
        };

        var legendLine = new Line
        {
            X1 = 0,
            X2 = 24,
            Y1 = 6,
            Y2 = 6,
            Stroke = stroke,
            StrokeThickness = 2
        };
        legendLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        row.Children.Add(legendLine);

        row.Children.Add(new TextBlock
        {
            Text = waveCount == 1 ? "光栅强度" : $"波长{waveIndex + 1}",
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        LegendPanel.Children.Add(row);
    }

    private void SetChartPlaceholder(double width, double height, string text)
    {
        CalibrationChartCanvas.Children.Clear();
        CalibrationChartHost.Width = width;
        CalibrationChartHost.Height = height;
        CalibrationChartCanvas.Children.Add(CalibrationChartHost);
        _chartPanel.SetPlaceholder(text);
        MarkCalibrationChartRendered(width, height);
    }

    private FormattedText CreateChartText(string text, double fontSize)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"),
            fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private void DrawPlaceholder(double width, double height, string text)
    {
        var placeholder = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 14
        };
        placeholder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(placeholder, (width - placeholder.DesiredSize.Width) / 2.0);
        Canvas.SetTop(placeholder, (height - placeholder.DesiredSize.Height) / 2.0);
        CalibrationChartCanvas.Children.Add(placeholder);
        MarkCalibrationChartRendered(width, height);
    }

    private float TryGetThresholdOrDefault()
    {
        return TryParseUiFloat(ThresholdTextBox.Text, out float threshold) && threshold > 0
            ? threshold
            : 0.5f;
    }

    private void ResetCalibrationChartZoom()
    {
        _chartZoomXMin = null;
        _chartZoomXMax = null;
        _chartZoomYMin = null;
        _chartZoomYMax = null;
        RequestCalibrationChartRender();
    }

    private void RequestCalibrationChartRender()
    {
        _chartRenderInvalidated = true;
        _chartRenderTimer.Stop();
        _chartRenderTimer.Start();
    }

    private void ApplyCalibrationChartZoom(ref float xMin, ref float xMax, ref float yMin, ref float yMax)
    {
        if (_chartZoomXMin.HasValue && _chartZoomXMax.HasValue)
        {
            xMin = _chartZoomXMin.Value;
            xMax = _chartZoomXMax.Value;
        }

        if (_chartZoomYMin.HasValue && _chartZoomYMax.HasValue)
        {
            yMin = _chartZoomYMin.Value;
            yMax = _chartZoomYMax.Value;
        }
    }

    private bool TryGetCalibrationChartBounds(out Rect plotRect, out float xMin, out float xMax, out float yMin, out float yMax)
    {
        plotRect = default;
        xMin = 0;
        xMax = 0;
        yMin = 0;
        yMax = 0;

        double width = CalibrationChartCanvas.ActualWidth;
        double height = CalibrationChartCanvas.ActualHeight;
        if (width < 160 || height < 120 || _latestCalibrationWaveData is null)
        {
            return false;
        }

        int waveCount = _latestCalibrationWaveData.WaveCount;
        int sampleCount = _latestCalibrationWaveData.SampleCount;
        float[] values = _latestCalibrationWaveData.WaveValues;
        int expected = waveCount * sampleCount;
        if (waveCount <= 0 || sampleCount <= 0 || values.Length < expected)
        {
            return false;
        }

        const double left = 64;
        const double top = 26;
        const double right = 18;
        const double bottom = 42;
        plotRect = new Rect(left, top, Math.Max(10, width - left - right), Math.Max(10, height - top - bottom));

        return TryGetCalibrationDataBounds(out xMin, out xMax, out yMin, out yMax);
    }

    private bool TryGetCalibrationDataBounds(out float xMin, out float xMax, out float yMin, out float yMax)
    {
        xMin = 0;
        xMax = 0;
        yMin = 0;
        yMax = 0;

        if (_latestCalibrationWaveData is null)
        {
            return false;
        }

        int waveCount = _latestCalibrationWaveData.WaveCount;
        int sampleCount = _latestCalibrationWaveData.SampleCount;
        float[] values = _latestCalibrationWaveData.WaveValues;
        int expected = waveCount * sampleCount;
        if (waveCount <= 0 || sampleCount <= 0 || values.Length < expected)
        {
            return false;
        }

        if (_cachedBoundsWaveTimestampMs == _latestCalibrationWaveData.TimestampMs &&
            _cachedBoundsChannel == _latestCalibrationWaveData.Channel &&
            _cachedBoundsWaveCount == waveCount &&
            _cachedBoundsSampleCount == sampleCount)
        {
            xMin = _cachedBoundsXMin;
            xMax = _cachedBoundsXMax;
            yMin = _cachedBoundsYMin;
            yMax = _cachedBoundsYMax;
            return true;
        }

        float positionScaleToMeters = GetPositionScaleToMeters(sampleCount);
        float nextXMin = 0.0f;
        float nextXMax = Math.Max(positionScaleToMeters, Math.Max(1, sampleCount - 1) * positionScaleToMeters);

        float nextYMin = float.MaxValue;
        float nextYMax = float.MinValue;
        for (int i = 0; i < expected; i++)
        {
            float value = values[i];
            if (!float.IsFinite(value))
            {
                continue;
            }

            nextYMin = Math.Min(nextYMin, value);
            nextYMax = Math.Max(nextYMax, value);
        }

        if (!float.IsFinite(nextYMin) || !float.IsFinite(nextYMax) || nextYMin == float.MaxValue || nextYMax == float.MinValue)
        {
            return false;
        }

        if (Math.Abs(nextYMax - nextYMin) < 1e-6f)
        {
            nextYMin -= 1.0f;
            nextYMax += 1.0f;
        }
        else
        {
            float padding = (nextYMax - nextYMin) * 0.08f;
            nextYMin -= padding;
            nextYMax += padding;
        }

        _cachedBoundsWaveTimestampMs = _latestCalibrationWaveData.TimestampMs;
        _cachedBoundsChannel = _latestCalibrationWaveData.Channel;
        _cachedBoundsWaveCount = waveCount;
        _cachedBoundsSampleCount = sampleCount;
        _cachedBoundsXMin = nextXMin;
        _cachedBoundsXMax = nextXMax;
        _cachedBoundsYMin = nextYMin;
        _cachedBoundsYMax = nextYMax;

        xMin = nextXMin;
        xMax = nextXMax;
        yMin = nextYMin;
        yMax = nextYMax;
        return true;
    }

    private void EnsureCalibrationChartZoomInitialized(float fullXMin, float fullXMax, float fullYMin, float fullYMax)
    {
        _chartZoomXMin ??= fullXMin;
        _chartZoomXMax ??= fullXMax;
        _chartZoomYMin ??= fullYMin;
        _chartZoomYMax ??= fullYMax;
    }

    private static void ApplyZoom(ref float? rangeMin, ref float? rangeMax, float fullMin, float fullMax, float center, float factor, float minSpan)
    {
        if (!rangeMin.HasValue || !rangeMax.HasValue)
        {
            rangeMin = fullMin;
            rangeMax = fullMax;
        }

        float currentMin = rangeMin!.Value;
        float currentMax = rangeMax!.Value;
        float currentSpan = Math.Max(minSpan, currentMax - currentMin);
        float nextSpan = Math.Clamp(currentSpan * factor, minSpan, Math.Max(minSpan, fullMax - fullMin));
        float ratio = currentSpan <= 0 ? 0.5f : (center - currentMin) / currentSpan;
        ratio = Math.Clamp(ratio, 0.0f, 1.0f);

        float nextMin = center - nextSpan * ratio;
        float nextMax = nextMin + nextSpan;

        if (nextMin < fullMin)
        {
            nextMax += fullMin - nextMin;
            nextMin = fullMin;
        }

        if (nextMax > fullMax)
        {
            nextMin -= nextMax - fullMax;
            nextMax = fullMax;
        }

        nextMin = Math.Max(fullMin, nextMin);
        nextMax = Math.Min(fullMax, nextMax);

        if (nextMax - nextMin < minSpan)
        {
            nextMax = Math.Min(fullMax, nextMin + minSpan);
            nextMin = Math.Max(fullMin, nextMax - minSpan);
        }

        rangeMin = nextMin;
        rangeMax = nextMax;
    }

    private static void ClampPanRange(ref float rangeMin, ref float rangeMax, float fullMin, float fullMax)
    {
        float span = Math.Max(0.0001f, rangeMax - rangeMin);
        float fullSpan = Math.Max(0.0001f, fullMax - fullMin);
        if (span >= fullSpan)
        {
            rangeMin = fullMin;
            rangeMax = fullMax;
            return;
        }

        if (rangeMin < fullMin)
        {
            rangeMax += fullMin - rangeMin;
            rangeMin = fullMin;
        }

        if (rangeMax > fullMax)
        {
            rangeMin -= rangeMax - fullMax;
            rangeMax = fullMax;
        }

        rangeMin = Math.Max(fullMin, rangeMin);
        rangeMax = Math.Min(fullMax, rangeMax);
    }

    private static bool TryParseUiFloat(string? text, out float value)
    {
        return float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.GetCultureInfo("zh-CN"), out value);
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }

    private void CalibrationSensorGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is not DataGridRow)
        {
            return;
        }

        e.Handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CalibrationSensorGrid.ContextMenu is ContextMenu menu)
            {
                menu.PlacementTarget = CalibrationSensorGrid;
                menu.IsOpen = true;
            }
        }), DispatcherPriority.Input);
    }

    private void CalibrationSensorGridRow_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CalibrationSensorGrid.ContextMenu is ContextMenu menu)
            {
                menu.PlacementTarget = CalibrationSensorGrid;
                menu.IsOpen = true;
            }
        }), DispatcherPriority.Input);
    }

    private void CalibrationSensorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalibrationSensorGrid.SelectedItem is CalibrationRowItem row)
        {
            _selectedRowSourceIndexByChannel[_selectedChannelIndex] = row.SourceIndex;
        }
        else
        {
            _selectedRowSourceIndexByChannel.Remove(_selectedChannelIndex);
        }
    }

    private void CalibrationSensorGridContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = true;
    }

    private void CalibrationSensorGridContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
    }

    private void CalibrationSensorGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
        {
            return;
        }

        DeleteCalibrationRowMenuItem_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void DeleteCalibrationRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CalibrationRowItem? row = CalibrationSensorGrid.SelectedItem as CalibrationRowItem;
        if (row is null)
        {
            AppMessageDialog.ShowInfo(this, "校准", "请先左键选中要删除的条目，再执行删除。");
            return;
        }

        if (!_deletedSourceIndexesByChannel.TryGetValue(_selectedChannelIndex, out HashSet<int>? deleted))
        {
            deleted = new HashSet<int>();
            _deletedSourceIndexesByChannel[_selectedChannelIndex] = deleted;
        }

        deleted.Add(row.SourceIndex);
        _selectedRowSourceIndexByChannel.Remove(_selectedChannelIndex);
        _calibrationRows.Remove(row);
        _persistEditedCalibrationRows(_selectedChannelIndex, _calibrationRows.ToList());

        for (int i = 0; i < _calibrationRows.Count; i++)
        {
            _calibrationRows[i].Index = i + 1;
        }

        StatusTextBlock.Text = $"CH{_selectedChannelIndex + 1} 已删除 1 条校准记录。";
    }

    private void ApplyCalibrationRows(IReadOnlyList<CalibrationRowItem> rows)
    {
        _calibrationRows.Clear();
        foreach (CalibrationRowItem row in rows.OrderBy(x => x.Index).ThenBy(x => x.SourceIndex))
        {
            _calibrationRows.Add(new CalibrationRowItem
            {
                SourceIndex = row.SourceIndex,
                RelativeSamplePoint = row.RelativeSamplePoint,
                Index = row.Index,
                SamplePoint = row.SamplePoint,
                PositionM = row.PositionM,
                WaveIndex = row.WaveIndex
            });
        }

        if (_selectedRowSourceIndexByChannel.TryGetValue(_selectedChannelIndex, out int selectedSourceIndex))
        {
            CalibrationRowItem? selectedRow = _calibrationRows.FirstOrDefault(x => x.SourceIndex == selectedSourceIndex);
            if (selectedRow is not null)
            {
                CalibrationSensorGrid.SelectedItem = selectedRow;
                CalibrationSensorGrid.ScrollIntoView(selectedRow);
                return;
            }

            _selectedRowSourceIndexByChannel.Remove(_selectedChannelIndex);
        }

        CalibrationSensorGrid.SelectedItem = null;
    }

    public sealed class CalibrationWindowParameters
    {
        public int ChannelIndex { get; init; }
        public IReadOnlyList<int> AvailableChannelIndexes { get; init; } = Array.Empty<int>();
        public float Threshold { get; init; }
        public int CalibrationCurrentMa { get; init; }
        public int FiberLengthM { get; init; }
        public float StartLengthM { get; init; }
        public float[] CenterWavelengths { get; init; } = Array.Empty<float>();
        public bool MultiWaveReverse { get; init; }
    }

    public sealed class CalibrationRowItem
    {
        public int SourceIndex { get; init; }
        public int RelativeSamplePoint { get; init; }
        public int Index { get; set; }
        public int SamplePoint { get; init; }
        public float PositionM { get; init; }
        public string PositionText => PositionM.ToString("0.00", CultureInfo.InvariantCulture);
        public int WaveIndex { get; init; }
    }

    private float GetCurrentPositionScaleToMeters()
    {
        int sampleCount = _latestCalibrationWaveData?.SampleCount ?? 0;
        return GetPositionScaleToMeters(sampleCount);
    }

    private float GetPositionScaleToMeters(int sampleCount)
    {
        return CalibrationPositionScaleToMeters;
    }

    private bool AreWaveIndexesOneBased(int[] rawWaveIndexes)
    {
        if (_centerWavelengths.Length <= 0 || rawWaveIndexes.Length == 0)
        {
            return false;
        }

        int waveCount = _centerWavelengths.Length;
        int[] valid = rawWaveIndexes.Where(x => x >= 0).ToArray();
        return valid.Length > 0 &&
               valid.All(x => x >= 1 && x <= waveCount) &&
               valid.Any(x => x == waveCount) &&
               !valid.Any(x => x == 0);
    }

    private int NormalizeWaveIndexForDisplay(int rawWaveIndex, bool rawWaveIndexesAreOneBased)
    {
        if (_centerWavelengths.Length == 0)
        {
            return Math.Max(1, rawWaveIndexesAreOneBased ? rawWaveIndex : rawWaveIndex + 1);
        }

        int displayIndex = rawWaveIndexesAreOneBased ? rawWaveIndex : rawWaveIndex + 1;
        return Math.Clamp(displayIndex, 1, _centerWavelengths.Length);
    }

    private int? InferDisplayWaveIndexFromWaveData(int samplePoint)
    {
        CalibrationWaveDataModel? waveData = _latestCalibrationWaveData;
        if (waveData is null ||
            waveData.Channel != _selectedChannelIndex ||
            waveData.WaveCount <= 0 ||
            waveData.SampleCount <= 0 ||
            waveData.WaveValues.Length < waveData.WaveCount * waveData.SampleCount)
        {
            return null;
        }

        int sampleIndex = Math.Clamp(samplePoint, 0, waveData.SampleCount - 1);
        int radius = Math.Min(2, Math.Max(0, waveData.SampleCount - 1));
        int bestWaveIndex = -1;
        float bestValue = float.NegativeInfinity;
        for (int waveIndex = 0; waveIndex < waveData.WaveCount; waveIndex++)
        {
            int offset = waveIndex * waveData.SampleCount;
            int from = Math.Max(0, sampleIndex - radius);
            int to = Math.Min(waveData.SampleCount - 1, sampleIndex + radius);
            float localBest = float.NegativeInfinity;
            for (int i = from; i <= to; i++)
            {
                float value = waveData.WaveValues[offset + i];
                if (float.IsFinite(value) && value > localBest)
                {
                    localBest = value;
                }
            }

            if (localBest > bestValue)
            {
                bestValue = localBest;
                bestWaveIndex = waveIndex;
            }
        }

        return bestWaveIndex >= 0
            ? Math.Clamp(bestWaveIndex + 1, 1, Math.Max(1, waveData.WaveCount))
            : null;
    }

    private void UpdateAvailableChannels(IReadOnlyList<int>? channels, int preferredChannel)
    {
        _availableChannelIndexes.Clear();

        if (channels is not null)
        {
            foreach (int channel in channels.Distinct().Where(x => x >= 0).OrderBy(x => x))
            {
                _availableChannelIndexes.Add(channel);
            }
        }

        if (_availableChannelIndexes.Count == 0)
        {
            _availableChannelIndexes.Add(Math.Max(0, preferredChannel));
        }

        ChannelComboBox.Items.Clear();
        foreach (int channelIndex in _availableChannelIndexes)
        {
            ChannelComboBox.Items.Add($"CH{channelIndex + 1}");
        }
    }

    private int ResolveAvailableChannel(int preferredChannel)
    {
        if (_availableChannelIndexes.Count == 0)
        {
            return Math.Max(0, preferredChannel);
        }

        if (_availableChannelIndexes.Contains(preferredChannel))
        {
            return preferredChannel;
        }

        return _availableChannelIndexes[0];
    }

    private sealed class CalibrationChartPanel : WF.Control
    {
        private readonly CalibrationWindow _owner;
        private float[] _values = Array.Empty<float>();
        private int _waveCount;
        private int _sampleCount;
        private float _positionScaleToMeters = CalibrationPositionScaleToMeters;
        private float _xMin;
        private float _xMax = 1;
        private float _yMin;
        private float _yMax = 1;
        private string? _placeholder;

        public CalibrationChartPanel(CalibrationWindow owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            BackColor = SD.Color.FromArgb(7, 28, 56);
            TabStop = true;
            SetStyle(
                WF.ControlStyles.AllPaintingInWmPaint |
                WF.ControlStyles.UserPaint |
                WF.ControlStyles.OptimizedDoubleBuffer |
                WF.ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Focus();
            base.OnMouseEnter(e);
        }

        public void SetPlaceholder(string text)
        {
            _placeholder = text;
            _values = Array.Empty<float>();
            Invalidate();
        }

        public void SetChart(
            float[] values,
            int waveCount,
            int sampleCount,
            float positionScaleToMeters,
            float xMin,
            float xMax,
            float yMin,
            float yMax)
        {
            _placeholder = null;
            _values = values;
            _waveCount = waveCount;
            _sampleCount = sampleCount;
            _positionScaleToMeters = positionScaleToMeters;
            _xMin = xMin;
            _xMax = xMax;
            _yMin = yMin;
            _yMax = yMax;
            Invalidate();
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            base.OnPaint(e);
            SD.Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = SD2.PixelOffsetMode.Half;

            if (!string.IsNullOrWhiteSpace(_placeholder))
            {
                using var font = new SD.Font("Microsoft YaHei", 10.5f);
                SD.SizeF size = g.MeasureString(_placeholder, font);
                using var brush = new SD.SolidBrush(SD.Color.White);
                g.DrawString(_placeholder, font, brush, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
                return;
            }

            if (_values.Length == 0 || _waveCount <= 0 || _sampleCount <= 0)
            {
                return;
            }

            const float left = 72f;
            const float top = 34f;
            const float right = 30f;
            const float bottom = 58f;
            var plot = new SD.RectangleF(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            DrawGrid(g, plot);

            float xSpan = Math.Max(0.0001f, _xMax - _xMin);
            float ySpan = Math.Max(0.0001f, _yMax - _yMin);
            int firstSampleIndex = Math.Clamp((int)Math.Floor(_xMin / _positionScaleToMeters), 0, _sampleCount - 1);
            int lastSampleIndex = Math.Clamp((int)Math.Ceiling(_xMax / _positionScaleToMeters), firstSampleIndex, _sampleCount - 1);
            int visibleSampleCount = Math.Max(1, lastSampleIndex - firstSampleIndex + 1);
            int sampleStep = Math.Max(1, visibleSampleCount / MaxRenderedSamplesPerWave);

            g.SetClip(plot);
            for (int waveIndex = 0; waveIndex < _waveCount; waveIndex++)
            {
                SD.Color color = ToDrawingColor(_owner._waveBrushes[waveIndex % _owner._waveBrushes.Length]);
                using var pen = new SD.Pen(color, 1.35f)
                {
                    LineJoin = SD2.LineJoin.Round,
                    StartCap = SD2.LineCap.Round,
                    EndCap = SD2.LineCap.Round
                };
                int offset = waveIndex * _sampleCount;
                var points = new List<SD.PointF>(Math.Min(MaxRenderedSamplesPerWave + 2, visibleSampleCount));
                for (int sampleIndex = firstSampleIndex; sampleIndex <= lastSampleIndex; sampleIndex += sampleStep)
                {
                    float y = _values[offset + sampleIndex];
                    if (!float.IsFinite(y))
                    {
                        continue;
                    }

                    float samplePositionM = sampleIndex * _positionScaleToMeters;
                    float px = plot.Left + (samplePositionM - _xMin) / xSpan * plot.Width;
                    float py = plot.Bottom - (y - _yMin) / ySpan * plot.Height;
                    points.Add(new SD.PointF(px, py));
                }

                if (points.Count > 1)
                {
                    g.DrawLines(pen, points.ToArray());
                }
            }
            g.ResetClip();
            DrawLegend(g, plot);
        }

        private void DrawGrid(SD.Graphics g, SD.RectangleF plot)
        {
            using var borderPen = new SD.Pen(SD.Color.FromArgb(138, 138, 138), 1f);
            using var gridPen = new SD.Pen(SD.Color.FromArgb(102, 102, 102), 1f);
            using var textBrush = new SD.SolidBrush(SD.Color.White);
            using var labelFont = new SD.Font("Microsoft YaHei", 9f);
            using var titleFont = new SD.Font("Microsoft YaHei", 9.75f);
            g.DrawRectangle(borderPen, plot.X, plot.Y, plot.Width, plot.Height);

            for (int i = 0; i <= 4; i++)
            {
                float y = plot.Top + plot.Height * i / 4f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                string label = (_yMax - i / 4f * (_yMax - _yMin)).ToString("0.00", CultureInfo.InvariantCulture);
                SD.SizeF size = g.MeasureString(label, labelFont);
                g.DrawString(label, labelFont, textBrush, plot.Left - 18 - size.Width, y - 10);
            }

            for (int i = 0; i <= 4; i++)
            {
                float x = plot.Left + plot.Width * i / 4f;
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                string label = (_xMin + i / 4f * (_xMax - _xMin)).ToString("0.0", CultureInfo.InvariantCulture);
                SD.SizeF size = g.MeasureString(label, labelFont);
                g.DrawString(label, labelFont, textBrush, x - size.Width / 2f, plot.Bottom + 8);
            }

            const string title = "距离(m)";
            SD.SizeF titleSize = g.MeasureString(title, titleFont);
            g.DrawString(title, titleFont, textBrush, plot.Left + plot.Width / 2f - titleSize.Width / 2f, plot.Bottom + 30);
        }

        private void DrawLegend(SD.Graphics g, SD.RectangleF plot)
        {
            if (_waveCount <= 0)
            {
                return;
            }

            using var font = new SD.Font("Microsoft YaHei", 9f);
            using var textBrush = new SD.SolidBrush(SD.Color.White);
            using var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(210, 16, 40, 76));
            using var borderPen = new SD.Pen(SD.Color.FromArgb(90, 142, 201), 1f);

            string[] labels = Enumerable.Range(0, _waveCount)
                .Select(i => _waveCount == 1 ? "光栅强度" : $"波长{i + 1}")
                .ToArray();
            float maxLabelWidth = labels.Select(label => g.MeasureString(label, font).Width).DefaultIfEmpty(0).Max();
            float rowHeight = 20f;
            float legendWidth = Math.Max(92f, 34f + maxLabelWidth + 16f);
            float legendHeight = 8f + rowHeight * _waveCount + 8f;
            float legendLeft = plot.Right - legendWidth - 10f;
            float legendTop = plot.Top + 10f;

            var legendRect = new SD.RectangleF(legendLeft, legendTop, legendWidth, legendHeight);
            g.FillRectangle(bgBrush, legendRect);
            g.DrawRectangle(borderPen, legendRect.X, legendRect.Y, legendRect.Width, legendRect.Height);

            for (int waveIndex = 0; waveIndex < _waveCount; waveIndex++)
            {
                float y = legendTop + 8f + waveIndex * rowHeight + rowHeight / 2f;
                SD.Color color = ToDrawingColor(_owner._waveBrushes[waveIndex % _owner._waveBrushes.Length]);
                using var linePen = new SD.Pen(color, 2f);
                g.DrawLine(linePen, legendLeft + 10f, y, legendLeft + 32f, y);
                g.DrawString(labels[waveIndex], font, textBrush, legendLeft + 38f, y - 8f);
            }
        }

        private static SD.Color ToDrawingColor(Brush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                Color color = solid.Color;
                return SD.Color.FromArgb(color.A, color.R, color.G, color.B);
            }

            return SD.Color.White;
        }
    }
}
