using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using IoPath = System.IO.Path;
using LineShape = System.Windows.Shapes.Line;
using PolylineShape = System.Windows.Shapes.Polyline;
using RectangleShape = System.Windows.Shapes.Rectangle;
using DtsMonitor.App.Models;
using DtsMonitor.App.Services;

namespace DtsMonitor.App;

public partial class MainWindow : Window
{
    private const double MainToolbarCompactThreshold = 1760d;
    private const double MainToolbarTightThreshold = 1500d;
    private static readonly double[] FixedTempThresholdOptions = { 60d, 70d, 85d, 105d, 138d, 180d };
    private static readonly double[] RateThresholdOptions = { 10d, 20d, 30d };
    private const int DefaultProfileStepMeters = 5;
    // The native layer rejects DTS_SetBasicConfig when target_profile_points < 16
    // (dts_core.cpp). Short fibers with the default step would compute fewer points,
    // so floor the value here to keep the config valid.
    private const int MinProfilePoints = 16;
    private const int DefaultSensorCount = 24;
    private const int DisplayChannelBase = 1;
    private const int MaxMonitorChannels = 32;
    private const int SingleSensorTrendPointLimit = 10;
    private const double ChartPlotLeft = 86;
    private const double WavelengthChartPlotLeft = 90;
    private const double ChartPlotRight = 24;
    private const double ChartPlotTop = 10;
    private const double ChartPlotBottom = 52;
    private const double TemperatureChartPlotTop = ChartPlotTop;
    private const double XAxisTickLabelOffset = 8;
    private const double XAxisTitleOffset = 28;
    private const float SensorRawPositionScaleToMeters = 0.1f;
    private const int MaxChartRenderPoints = 2500;
    private const int MaxChartMarkerRenderPoints = 120;
    private const int ShapeBaselineAverageFrameCount = 30;
    private const int ShapeBaselineMinimumFrameCount = 5;
    private const int ShapeRealtimeMedianFrameCount = 5;
    private const float ShapeBaselineStdWarnPm = 10.0f;
    private const float StrainDisplayHalfRangeMicro = 100.0f;
    private const float SingleFiberShapeDisplayHalfRangeM = 0.10f;
    private static readonly TimeSpan SensorListRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShapeSensingRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly bool AlarmFeaturesEnabled = true;

    private readonly ObservableCollection<RealtimeAlarmRow> _realtimeAlarmRows = new();
    private readonly ObservableCollection<RuntimeLogItem> _runtimeLogItems = new();
    private readonly ObservableCollection<ZoneParameterItem> _zoneParameterItems = new();
    private readonly ObservableCollection<AlarmRecord> _historyAlarmItems = new();
    private readonly List<AlarmRecord> _historyQueryRows = new();
    private readonly Dictionary<string, List<RealtimeAlarmRowState>> _realtimeAlarmRowsByDeviceId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceRuntimeCache> _runtimeCacheByDeviceId = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ChannelOption> _monitorChannelOptions = new();
    private readonly ObservableCollection<ChannelOption> _channelOptions = new();
    private readonly ObservableCollection<ParameterChannelSettingItem> _parameterChannelSettings = new();
    private readonly ObservableCollection<SensorInfoRow> _sensorInfoRows = new();
    private readonly ObservableCollection<DeviceDefinition> _devices = new();
    private readonly Dictionary<int, SnapshotModel> _snapshotsByChannel = new();
    private readonly Dictionary<int, long> _lastSnapshotTimestampMsByChannel = new();
    private readonly Dictionary<int, double> _snapshotFrequencyHzByChannel = new();
    private long _lastSnapshotTimestampMsOverall;
    private double _snapshotFrequencyHzOverall;
    private readonly Dictionary<(int Channel, int SensorIndex), List<float>> _singleSensorTemperatureTrendByKey = new();
    private readonly Dictionary<(int Channel, int SensorIndex), List<float>> _singleSensorWavelengthTrendByKey = new();
    private readonly Dictionary<(int Channel, int SensorIndex), List<float>> _singleSensorStrainTrendByKey = new();
    private readonly Dictionary<int, float[]> _shapeReferenceTopByChannel = new();
    private readonly Dictionary<int, float[]> _shapeReferenceBottomByChannel = new();
    private readonly Dictionary<int, float[]> _latestAxialStrainByChannel = new();
    private readonly object _shapeWavelengthHistorySync = new();
    private readonly Dictionary<int, Queue<float[]>> _shapeWavelengthHistoryByChannel = new();
    private readonly ChartViewportState _sensorSpectrumViewport = new();
    private readonly ChartViewportState _spectrumViewport = new();
    private readonly ChartViewportState _temperatureViewport = new();
    private readonly ChartViewportState _singleSensorWavelengthViewport = new();
    private readonly ChartViewportState _singleSensorTemperatureViewport = new();
    private readonly ChartViewportState _singleSensorStrainViewport = new();
    private readonly ChartViewportState _strainArrayViewport = new();
    private readonly ChartViewportState _shapeReconstructionViewport = new();
    private readonly ChartViewportState _shapeReconstructionZoomViewport = new();
    private readonly ToolTip _chartHoverToolTip = new();

    private DeviceRegistry? _deviceRegistry;
    private AppLogger? _multiDeviceLogger;
    private readonly Dictionary<string, DeviceSessionProxy> _deviceSessions = new(StringComparer.Ordinal);
    private DeviceDefinition? _currentDevice;
    private DeviceViewState? _currentDeviceView;
    private DeviceSessionProxy? _service;
    private CalibrationWindow? _calibrationWindow;
    private ShapeMonitorWindow? _shapeMonitorWindow;
    private Window? _shapeReconstructionZoomWindow;
    private Canvas? _shapeReconstructionZoomCanvas;
    private Canvas? _shapeReconstructionZoomSourceCanvas;
    private TextBlock? _shapeReconstructionZoomTitleBlock;
    private string _shapeReconstructionZoomTitle = string.Empty;
    private HardwareConfig _config = new();
    private DispatcherTimer? _statusTimer;
    private SnapshotModel? _lastSnapshot;
    private readonly AppliedParameterState _appliedParameterState = new();
    private readonly Dictionary<int, string> _coefficientFilePathsByChannel = new();
    private readonly Dictionary<int, LoadedCoefficientProfile> _loadedCoefficientProfilesByChannel = new();
    private readonly Dictionary<int, float> _calibrationThresholdsByChannel = new();
    private readonly Dictionary<int, List<CalibrationWindow.CalibrationRowItem>> _editedCalibrationRowsByChannel = new();
    private readonly Dictionary<int, AlarmChannelEditorState> _alarmChannelStatesByChannel = new();
    private readonly Dictionary<int, AlarmChannelEditorState> _alarmChannelDraftStatesByChannel = new();
    private List<ZoneParameterItem> _appliedZoneParameterItems = new();
    private string _uiStatePath = string.Empty;
    private readonly Dictionary<int, string> _appliedHardwareConfigFingerprintsByChannel = new();
    private bool? _appliedAllChannelsLowSpeedMode;
    private bool _isRestoringUiState;
    private LoadedCoefficientProfile? _loadedCoefficientProfile;
    private int _activeCoefficientChannel = -1;
    private int _selectedMonitorChannel = 0;
    private bool _monitorAllChannels;
    private float? _temperatureAxisMinOverride;
    private float? _temperatureAxisMaxOverride;
    private ChartSeriesData? _sensorSpectrumChartData;
    private ChartSeriesData? _spectrumChartData;
    private ChartSeriesData? _temperatureChartData;
    private ChartSeriesData? _singleSensorWavelengthChartData;
    private ChartSeriesData? _singleSensorTemperatureChartData;
    private ChartSeriesData? _singleSensorStrainChartData;
    private ChartSeriesData? _strainArrayChartData;
    private ChartSeriesData? _shapeReconstructionChartData;
    private ShapeReconstructionResult? _latestShapeResult;
    private ChartSelectionState? _chartSelectionState;
    private bool _isSynchronizingGraphSelection;
    private string? _pendingConnectionConfirmationIp;
    private bool _pendingAutoRunAfterConnect;
    private bool _isSwitchingZoneChannel;
    private int _activeZoneEditorChannel = -1;
    private bool _isAlarmDialogOpen;
    private bool _isSwitchingDevice;
    private bool _isUpdatingEnableAllDevicesCheckBox;
    private readonly object _snapshotUiUpdateSync = new();
    private SnapshotModel? _pendingUiSnapshot;
    private bool _isSnapshotUiUpdatePending;
    private DateTime _lastSensorListRefreshUtc = DateTime.MinValue;
    private int _lastSensorListRefreshChannel = -1;
    private int _lastSensorListRefreshCount = -1;
    private DateTime _lastShapeSensingRefreshUtc = DateTime.MinValue;
    private long _lastShapeSensingTimestampMs = -1;

    public MainWindow()
    {
        InitializeComponent();

        _chartHoverToolTip.Placement = PlacementMode.MousePoint;
        _chartHoverToolTip.HorizontalOffset = 14;
        _chartHoverToolTip.VerticalOffset = 16;
        _chartHoverToolTip.StaysOpen = true;
        _chartHoverToolTip.Background = BrushFromHex("#E60B2141");
        _chartHoverToolTip.BorderBrush = BrushFromHex("#2E5A8F");
        _chartHoverToolTip.Foreground = BrushFromHex("#E9F5FF");
        _chartHoverToolTip.Padding = new Thickness(8, 5, 8, 5);

        HistoryAlarmGrid.ItemsSource = _historyAlarmItems;
        RealtimeAlarmGrid.ItemsSource = _realtimeAlarmRows;
        RuntimeLogGrid.ItemsSource = _runtimeLogItems;
        ZoneConfigGrid.ItemsSource = _zoneParameterItems;
        DeviceSelectorComboBox.ItemsSource = _devices;
        ChannelListBox.ItemsSource = _monitorChannelOptions;
        ChannelListBox.DisplayMemberPath = nameof(ChannelOption.DisplayText);
        ZoneChannelComboBox.ItemsSource = _channelOptions;
        ZoneChannelComboBox.DisplayMemberPath = nameof(ChannelOption.DisplayText);
        ChannelParameterItemsControl.ItemsSource = _parameterChannelSettings;
        SensorInfoGrid.ItemsSource = _sensorInfoRows;

        InitializeMonitorChannelOptions();
        InitializeParameterChannelSettings();
        InitializeHistoryFilterSelectors();
    }

    private void SyncAcquisitionParameterSelectorsFromTextValues()
    {
        SelectComboBoxItemByTag(LaserTypeComboBox, LaserTypeTextBox?.Text, 0);
        SelectComboBoxItemByTag(SpeedModeComboBox, SpeedModeTextBox?.Text, 0);
        SelectComboBoxItemByTag(AlgorithmTypeComboBox, AlgorithmTypeTextBox?.Text, 0);
        SelectComboBoxItemByTag(WavelengthPrecisionModeComboBox, WavelengthPrecisionModeTextBox?.Text, 0);
        SelectComboBoxItemByTag(FiberDensityModeComboBox, FiberDensityTextBox?.Text, 0);
    }

    private static void SelectComboBoxItemByTag(ComboBox? comboBox, string? codeText, int fallback)
    {
        if (comboBox is null)
        {
            return;
        }

        int desired = int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem &&
                int.TryParse(comboItem.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tag) &&
                tag == desired)
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static int GetSelectedComboTagOrDefault(ComboBox? comboBox, int fallback)
    {
        if (comboBox?.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return fallback;
    }

    private void UpdateComputedProfilePoints()
    {
        if (int.TryParse(FiberLengthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength) &&
            int.TryParse(ProfileStepTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStep) &&
            parsedLength > 0 &&
            parsedStep > 0)
        {
            TargetPointsTextBox.Text = CalcProfilePointsByStep(parsedLength, parsedStep).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void NumericSpinnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        string[] parts = tag.Split('|');
        if (parts.Length != 2)
        {
            return;
        }

        if (FindName(parts[0]) is not TextBox target)
        {
            return;
        }

        if (!decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal delta))
        {
            return;
        }

        decimal current = 0m;
        if (!string.IsNullOrWhiteSpace(target.Text))
        {
            decimal.TryParse(target.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out current);
        }

        decimal updated = current + delta;
        int decimals = parts[1].Contains('.') ? parts[1].Length - parts[1].IndexOf('.') - 1 : 0;
        target.Text = decimals > 0
            ? updated.ToString($"F{decimals}", CultureInfo.InvariantCulture)
            : decimal.Truncate(updated).ToString(CultureInfo.InvariantCulture);

        if (ReferenceEquals(target, FiberLengthTextBox) || ReferenceEquals(target, ProfileStepTextBox))
        {
            UpdateComputedProfilePoints();
        }
    }

    private void LaserTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LaserTypeTextBox is null)
        {
            return;
        }

        LaserTypeTextBox.Text = GetSelectedComboTagOrDefault(LaserTypeComboBox, 0).ToString(CultureInfo.InvariantCulture);
    }

    private void SpeedModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedModeTextBox is null)
        {
            return;
        }

        SpeedModeTextBox.Text = GetSelectedComboTagOrDefault(SpeedModeComboBox, 0).ToString(CultureInfo.InvariantCulture);
    }

    private void AlgorithmTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlgorithmTypeTextBox is null)
        {
            return;
        }

        AlgorithmTypeTextBox.Text = GetSelectedComboTagOrDefault(AlgorithmTypeComboBox, 0).ToString(CultureInfo.InvariantCulture);
    }

    private void WavelengthPrecisionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WavelengthPrecisionModeTextBox is null)
        {
            return;
        }

        WavelengthPrecisionModeTextBox.Text = GetSelectedComboTagOrDefault(WavelengthPrecisionModeComboBox, 0).ToString(CultureInfo.InvariantCulture);
    }

    private void FiberDensityModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FiberDensityTextBox is null)
        {
            return;
        }

        FiberDensityTextBox.Text = GetSelectedComboTagOrDefault(FiberDensityModeComboBox, 0).ToString(CultureInfo.InvariantCulture);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveWindowLayout();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appRoot = IoPath.Combine(appData, "HG-FBG");
        string legacyUiStatePath = IoPath.Combine(appRoot, "ui_state.json");
        string devicesPath = IoPath.Combine(appRoot, "devices.json");
        _multiDeviceLogger = new AppLogger(IoPath.Combine(appRoot, "main.log"));
        _deviceRegistry = new DeviceRegistry(devicesPath);
        IReadOnlyList<DeviceDefinition> devices = _deviceRegistry.LoadOrCreateDefault(legacyUiStatePath);
        _devices.Clear();
        foreach (DeviceDefinition device in devices)
        {
            _devices.Add(device);
        }

        DateTime now = DateTime.Now;
        InitializeHistoryDateTimeSelectors(now);
        UpdateMainViewTabButtonStates();

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += async (_, _) =>
        {
            if (_service is not null && !_service.HasStateChangingCommandInFlight)
            {
                await _service.RefreshStatusAsync();
            }

            RefreshConnectionState();
        };
        _statusTimer.Start();

        DeviceDefinition? initialDevice = _devices.FirstOrDefault(x => x.Enabled);
        if (initialDevice is not null)
        {
            DeviceSelectorComboBox.SelectedItem = initialDevice;
        }
        else
        {
            AddRuntimeLog(_devices.Count == 0 ? "未发现设备配置。" : "当前没有已启用的设备。");
        }

        await Task.CompletedTask;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            WindowLayoutHelper.CenterCurrentSize(this);
            ApplyResponsiveMainToolbar();
            return;
        }

        ApplyAdaptiveWindowLayout();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveMainToolbar();
    }

    private async Task<DeviceSessionProxy> GetOrCreateSessionAsync(DeviceDefinition device)
    {
        if (_deviceSessions.TryGetValue(device.DeviceId, out DeviceSessionProxy? existing))
        {
            return existing;
        }

        if (_multiDeviceLogger is null)
        {
            throw new InvalidOperationException("多设备日志未初始化。");
        }

        var session = new DeviceSessionProxy(device, _multiDeviceLogger);
        await session.StartAsync();
        _deviceSessions[device.DeviceId] = session;
        return session;
    }

    private void AttachCurrentService(DeviceSessionProxy session)
    {
        _service = session;
        _service.SnapshotUpdated += OnSnapshotUpdated;
        if (AlarmFeaturesEnabled)
        {
            _service.AlarmRaised += OnAlarmRaised;
        }

        if (_currentDeviceView is not null)
        {
            _currentDeviceView.PropertyChanged -= CurrentDeviceView_PropertyChanged;
        }

        _currentDeviceView = session.ViewState;
        _currentDeviceView.PropertyChanged += CurrentDeviceView_PropertyChanged;
    }

    private void CurrentDeviceView_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshConnectionState), DispatcherPriority.Background);
            return;
        }

        RefreshConnectionState();
    }

    private async Task SwitchToDeviceAsync(DeviceDefinition device, bool attemptAutoConnect)
    {
        if (_isSwitchingDevice)
        {
            return;
        }

        _isSwitchingDevice = true;
        try
        {
            if (_currentDevice?.DeviceId == device.DeviceId && _service is not null)
            {
                if (attemptAutoConnect)
                {
                    _ = Dispatcher.BeginInvoke(new Action(AttemptStartupAutoConnect), DispatcherPriority.ApplicationIdle);
                }

                return;
            }

            SaveUiState();
            PersistRuntimeCacheForCurrentDevice();
            PersistRealtimeAlarmRowsForCurrentDevice();
            DetachCurrentService();

            bool isNewSession = !_deviceSessions.ContainsKey(device.DeviceId);
            DeviceSessionProxy session;
            try
            {
                session = await GetOrCreateSessionAsync(device);
            }
            catch (Exception ex)
            {
                AppMessageDialog.ShowInfo(
                    this,
                    "启动错误",
                    BuildDeviceSessionStartupErrorMessage(ex));
                return;
            }

            _currentDevice = device;
            _uiStatePath = device.UiStatePath;
            AttachCurrentService(session);

            ClearRuntimeStateForDeviceSwitch();
            LoadUiState();
            RestoreRuntimeCacheForCurrentDevice();
            EnsureZoneChannelSelection();
            EnsureAlarmChannelStatesInitialized();
            LoadZoneAlarmEditorStateForSelectedChannel();
            SyncAppliedAlarmStateFromUi();
            RestoreTemperatureAxisRangeFromUiState();
            EnsureGraphViewSelection();
            ApplyCurrentGraphViewState(redrawCharts: false);
            if (string.IsNullOrWhiteSpace(ChannelTextBox.Text))
            {
                ChannelTextBox.Text = DisplayChannelBase.ToString(CultureInfo.InvariantCulture);
            }

            _config = BuildConfigFromUi();
            OpticSwitchEnabledCheckBox.IsChecked = OpticSwitchEnabledTextBox.Text != "0";
            MultiWaveReverseCheckBox.IsChecked = MultiWaveReverseTextBox.Text != "0";
            SyncAcquisitionParameterSelectorsFromTextValues();
            SyncChannelSelections(_selectedMonitorChannel);
            EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
            if (isNewSession)
            {
                ApplyAlarmSettingsToService();
            }
            RestoreRealtimeAlarmRowsForCurrentDevice();
            RefreshSelectedChannelData(preserveScroll: false, ensureSelectedRowVisible: true);
            RefreshConnectionState();
            RedrawSelectedChannelViews();
            AddRuntimeLog($"已切换到设备：{device.Name}");

            if (attemptAutoConnect)
            {
                _ = Dispatcher.BeginInvoke(new Action(AttemptStartupAutoConnect), DispatcherPriority.ApplicationIdle);
            }
        }
        finally
        {
            _isSwitchingDevice = false;
        }
    }

    private void DetachCurrentService()
    {
        if (_service is not null)
        {
            _service.SnapshotUpdated -= OnSnapshotUpdated;
            if (AlarmFeaturesEnabled)
            {
                _service.AlarmRaised -= OnAlarmRaised;
            }
        }

        if (_currentDeviceView is not null)
        {
            _currentDeviceView.PropertyChanged -= CurrentDeviceView_PropertyChanged;
        }

        _currentDeviceView = null;
        _service = null;
    }

    private static string BuildDeviceSessionStartupErrorMessage(Exception ex)
    {
        if (ex is FileNotFoundException or DllNotFoundException)
        {
            return $"设备会话加载失败，请确认 worker 可执行文件和 SDK 依赖存在。\n\n{ex.Message}";
        }

        return $"设备会话加载失败。\n\n{ex.Message}";
    }

    private void ClearRuntimeStateForDeviceSwitch()
    {
        _lastSnapshot = null;
        _snapshotsByChannel.Clear();
        ClearShapeWavelengthHistory();
        _lastSnapshotTimestampMsByChannel.Clear();
        _snapshotFrequencyHzByChannel.Clear();
        _singleSensorTemperatureTrendByKey.Clear();
        _singleSensorWavelengthTrendByKey.Clear();
        _sensorInfoRows.Clear();
        _realtimeAlarmRows.Clear();
        _historyAlarmItems.Clear();
        _historyQueryRows.Clear();
        _lastSnapshotTimestampMsOverall = 0;
        _snapshotFrequencyHzOverall = 0;
        UpdateStatusCardsFromViewState(_currentDeviceView);
        UpdateCurrentMonitorChannelDisplay();
    }

    private void ApplyAdaptiveWindowLayout()
    {
        WindowLayoutHelper.FitAndCenter(this, 1640, 980);
        ApplyResponsiveMainToolbar();
    }

    private void ApplyResponsiveMainToolbar()
    {
        if (MainToolbarStatusPanel is null || MainToolbarActionsPanel is null)
        {
            return;
        }

        double width = ActualWidth > 0 ? ActualWidth : Width;
        bool compact = width < MainToolbarCompactThreshold;
        bool tight = width < MainToolbarTightThreshold;

        MainToolbarStatusPanel.Visibility = Visibility.Visible;
        MainToolbarStatusPanel.Margin = tight
            ? new Thickness(8, 0, 8, 0)
            : compact
                ? new Thickness(14, 0, 14, 0)
                : new Thickness(24, 0, 18, 0);

        foreach (object child in MainToolbarActionsPanel.Children)
        {
            switch (child)
            {
                case Button button:
                    button.Width = tight ? 78 : compact ? 86 : 96;
                    button.Height = tight ? 34 : 38;
                    button.FontSize = tight ? 13 : 14;
                    button.Padding = tight ? new Thickness(8, 0, 8, 0) : new Thickness(12, 0, 12, 0);
                    button.Margin = tight ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 8, 0);
                    break;
                case ComboBox comboBox when ReferenceEquals(comboBox, DeviceSelectorComboBox):
                    comboBox.Width = tight ? 176 : compact ? 196 : 220;
                    comboBox.Height = tight ? 34 : 38;
                    comboBox.FontSize = tight ? 13 : 14;
                    comboBox.Margin = tight ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 8, 0);
                    break;
                case TextBlock textBlock:
                    textBlock.FontSize = tight ? 13 : 14;
                    textBlock.Margin = tight ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 8, 0);
                    break;
            }
        }

        foreach (object child in MainToolbarStatusPanel.Children)
        {
            if (child is TextBlock textBlock)
            {
                textBlock.FontSize = tight ? 12 : 13;
                textBlock.Margin = textBlock.Name switch
                {
                    nameof(CoreStateText) => tight ? new Thickness(0, 0, 10, 0) : new Thickness(0, 0, 14, 0),
                    nameof(ConnectionText) => new Thickness(0),
                    _ => tight ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 7, 0)
                };
            }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveUiState();

        if (_statusTimer is not null)
        {
            _statusTimer.Stop();
        }

        DetachCurrentService();

        foreach (DeviceSessionProxy session in _deviceSessions.Values)
        {
            session.Dispose();
        }
        _deviceSessions.Clear();

        _calibrationWindow?.Close();
        _calibrationWindow = null;
        _shapeMonitorWindow?.Close();
        _shapeMonitorWindow = null;
        _shapeReconstructionZoomWindow?.Close();
        _shapeReconstructionZoomWindow = null;
        _shapeReconstructionZoomCanvas = null;
        _shapeReconstructionZoomSourceCanvas = null;
        _shapeReconstructionZoomTitleBlock = null;
    }

    private void RefreshConnectionState()
    {
        if (ConnectionText is null || CoreStateText is null)
        {
            return;
        }

        if (_service is null)
        {
            ConnectionText.Text = "未初始化";
            CoreStateText.Text = "未初始化";
            UpdateCommandButtonStates(null);
            UpdateStatusCardsFromViewState(null);
            return;
        }

        DeviceViewState view = _currentDeviceView ?? _service.ViewState;
        ConnectionText.Text = view.WorkerState switch
        {
            WorkerState.Offline => "Worker离线",
            WorkerState.Starting => "Worker启动中",
            WorkerState.Faulted => "Worker故障",
            _ => DeviceConnectionStateToText(view.DeviceState)
        };
        CoreStateText.Text = DeviceOperationalStateToText(view.DeviceState, _service.GetState());
        UpdateCommandButtonStates(view);
        UpdateStatusCardsFromViewState(view);
        if (ChannelListBox is not null)
        {
            bool allowChannelSwitch = view.DeviceState is not DeviceState.Running and not DeviceState.Calibrating;
            ChannelListBox.IsHitTestVisible = allowChannelSwitch;
            KeyboardNavigation.SetTabNavigation(ChannelListBox, allowChannelSwitch ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        }

        if (!string.IsNullOrWhiteSpace(_pendingConnectionConfirmationIp) &&
            view.DeviceState is DeviceState.Connected or DeviceState.Running or DeviceState.Calibrating)
        {
            FinalizeSuccessfulConnection(_pendingConnectionConfirmationIp, _pendingAutoRunAfterConnect);
        }
    }

    private void UpdateCommandButtonStates(DeviceViewState? view)
    {
        bool hasDevice = _currentDevice is not null;
        bool deviceEnabled = _currentDevice?.Enabled != false;
        bool allowReconnect = view?.WorkerState is WorkerState.Offline or WorkerState.Faulted;
        ConnectDeviceButton.IsEnabled = hasDevice && deviceEnabled && ((view?.CanConnect ?? false) || allowReconnect);
        StartRunToolbarButton.IsEnabled = hasDevice && deviceEnabled && (view?.CanStart ?? false);
        StopRunToolbarButton.IsEnabled = hasDevice && deviceEnabled && (view?.CanStop ?? false);
        StartCalibrationToolbarButton.IsEnabled = hasDevice && deviceEnabled && CanStartCalibration(view);
        if (ApplyConfigDialogButton is not null)
        {
            ApplyConfigDialogButton.IsEnabled = hasDevice && deviceEnabled && (view is null || !view.IsBusy);
        }
    }

    private void UpdateStatusCardsFromViewState(DeviceViewState? view)
    {
        if (CurrentAlarmCountText is not null)
        {
            CurrentAlarmCountText.Text = AlarmFeaturesEnabled
                ? (view?.CurrentAlarmCount ?? 0).ToString(CultureInfo.InvariantCulture)
                : "0";
        }

        if (WaveTimeTextBlock is not null && (view?.LastSnapshotTime.HasValue ?? false))
        {
            WaveTimeTextBlock.Text = view!.LastSnapshotTime!.Value.ToString("yyyy-MM-dd\nHH:mm:ss", CultureInfo.InvariantCulture);
        }
        else if (WaveTimeTextBlock is not null && _lastSnapshot is null)
        {
            WaveTimeTextBlock.Text = "--";
        }
    }

    private void PersistRealtimeAlarmRowsForCurrentDevice()
    {
        if (_currentDevice is null)
        {
            return;
        }

        _realtimeAlarmRowsByDeviceId[_currentDevice.DeviceId] = _realtimeAlarmRows
            .Select(x => new RealtimeAlarmRowState
            {
                TimeText = x.TimeText,
                ChannelIndex = x.ChannelIndex,
                ChannelText = x.ChannelText,
                SensorIndex = x.SensorIndex,
                TypeText = x.TypeText,
                PositionM = x.PositionM
            })
            .ToList();
    }

    private void PersistRuntimeCacheForCurrentDevice()
    {
        if (_currentDevice is null)
        {
            return;
        }

        _runtimeCacheByDeviceId[_currentDevice.DeviceId] = new DeviceRuntimeCache
        {
            LastSnapshot = _lastSnapshot,
            SnapshotsByChannel = new Dictionary<int, SnapshotModel>(_snapshotsByChannel),
            LastSnapshotTimestampMsByChannel = new Dictionary<int, long>(_lastSnapshotTimestampMsByChannel),
            SnapshotFrequencyHzByChannel = new Dictionary<int, double>(_snapshotFrequencyHzByChannel),
            LastSnapshotTimestampMsOverall = _lastSnapshotTimestampMsOverall,
            SnapshotFrequencyHzOverall = _snapshotFrequencyHzOverall,
            SingleSensorTemperatureTrendByKey = _singleSensorTemperatureTrendByKey.ToDictionary(
                x => x.Key,
                x => new List<float>(x.Value)),
            SingleSensorWavelengthTrendByKey = _singleSensorWavelengthTrendByKey.ToDictionary(
                x => x.Key,
                x => new List<float>(x.Value))
        };
    }

    private void RestoreRuntimeCacheForCurrentDevice()
    {
        if (_currentDevice is null ||
            !_runtimeCacheByDeviceId.TryGetValue(_currentDevice.DeviceId, out DeviceRuntimeCache? cache))
        {
            return;
        }

        _lastSnapshot = cache.LastSnapshot;

        _snapshotsByChannel.Clear();
        ClearShapeWavelengthHistory();
        foreach ((int channel, SnapshotModel snapshot) in cache.SnapshotsByChannel)
        {
            _snapshotsByChannel[channel] = snapshot;
        }

        _lastSnapshotTimestampMsByChannel.Clear();
        foreach ((int channel, long timestampMs) in cache.LastSnapshotTimestampMsByChannel)
        {
            _lastSnapshotTimestampMsByChannel[channel] = timestampMs;
        }

        _snapshotFrequencyHzByChannel.Clear();
        foreach ((int channel, double frequencyHz) in cache.SnapshotFrequencyHzByChannel)
        {
            _snapshotFrequencyHzByChannel[channel] = frequencyHz;
        }

        _lastSnapshotTimestampMsOverall = cache.LastSnapshotTimestampMsOverall;
        _snapshotFrequencyHzOverall = cache.SnapshotFrequencyHzOverall;

        _singleSensorTemperatureTrendByKey.Clear();
        foreach (((int channel, int sensorIndex) key, List<float> values) in cache.SingleSensorTemperatureTrendByKey)
        {
            _singleSensorTemperatureTrendByKey[key] = new List<float>(values);
        }

        _singleSensorWavelengthTrendByKey.Clear();
        foreach (((int channel, int sensorIndex) key, List<float> values) in cache.SingleSensorWavelengthTrendByKey)
        {
            _singleSensorWavelengthTrendByKey[key] = new List<float>(values);
        }
    }

    private void ClearShapeWavelengthHistory()
    {
        lock (_shapeWavelengthHistorySync)
        {
            _shapeWavelengthHistoryByChannel.Clear();
        }
    }

    private void RestoreRealtimeAlarmRowsForCurrentDevice()
    {
        _realtimeAlarmRows.Clear();
        if (_currentDevice is null ||
            !_realtimeAlarmRowsByDeviceId.TryGetValue(_currentDevice.DeviceId, out List<RealtimeAlarmRowState>? rows) ||
            rows.Count == 0)
        {
            return;
        }

        int seq = 1;
        foreach (RealtimeAlarmRowState row in rows)
        {
            _realtimeAlarmRows.Add(new RealtimeAlarmRow
            {
                Seq = seq++,
                TimeText = row.TimeText,
                ChannelIndex = row.ChannelIndex,
                ChannelText = row.ChannelText,
                SensorIndex = row.SensorIndex,
                TypeText = row.TypeText,
                PositionM = row.PositionM
            });
        }
    }

    private static string DeviceConnectionStateToText(DeviceState state)
    {
        return state switch
        {
            DeviceState.Connecting => "连接中",
            DeviceState.Disconnected => "未连接",
            DeviceState.Error => "异常",
            _ => "已连接"
        };
    }

    private static string DeviceOperationalStateToText(DeviceState state, int nativeState)
    {
        return state switch
        {
            DeviceState.Connecting => "连接中",
            DeviceState.Connected => "就绪",
            DeviceState.Running => "运行中",
            DeviceState.Calibrating => "校准中",
            DeviceState.Error => "错误",
            _ => StateToText(nativeState)
        };
    }

    private bool CanStartCalibration(DeviceViewState? view)
    {
        if (_service is null || _currentDevice is null || view is null)
        {
            return false;
        }

        if (view.WorkerState != WorkerState.Online || view.IsBusy)
        {
            return false;
        }

        return view.DeviceState == DeviceState.Connected || _service.GetConnect() == 1;
    }

    private static string StateToText(int state)
    {
        return state switch
        {
            0 => "未初始化",
            1 => "已初始化",
            2 => "已配置",
            3 => "就绪",
            4 => "运行中",
            5 => "校准中",
            6 => "错误",
            _ => state.ToString(CultureInfo.InvariantCulture)
        };
    }

    private string BuildFriendlyHardwareError(string action, int rc)
    {
        if (_service is null)
        {
            return $"{action}失败。";
        }

        string rawError = _service.GetLastError() ?? string.Empty;
        int state = _service.GetState();

        if (_service.GetConnect() != 1 && state == 0)
        {
            return "设备尚未连接，请先连接设备。";
        }

        if (rawError.Contains("invalid current state", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("requires ready state", StringComparison.OrdinalIgnoreCase) ||
            rc == -2)
        {
            if (state == 1 || state == 2)
            {
                return action switch
                {
                    "开始运行" => "设备已连接，但当前参数尚未同步到设备。请先保存采集参数或加载系数文件后再开始运行。",
                    "开始校准" => "设备已连接，但当前校准参数尚未同步到设备。请先保存采集参数并完成参数同步后再开始校准。",
                    "同步参数到设备" => "设备已连接，但当前参数尚未完成同步准备。请先确认系数文件和采集参数有效。",
                    _ => $"设备已连接，但当前参数尚未同步完成，暂时无法{action}。"
                };
            }

            return state switch
            {
                0 => "设备尚未初始化，请先连接设备。",
                4 => $"设备当前正在运行，请先停止运行，再{action}。",
                5 => $"设备当前正在校准，请先停止校准，再{action}。",
                _ => $"设备当前处于{StateToText(state)}，暂时无法{action}。"
            };
        }

        if (rawError.Contains("invalid channel", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action}失败：通道无效。";
        }

        if (rawError.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || rc == -3)
        {
            return "设备未连接，请先连接设备。";
        }

        // Worker/IPC level failures: the device worker process crashed, restarted, or
        // did not answer in time. These surface as the proxy's synthesized messages.
        if (rawError.Contains("Worker not connected", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action}失败：后台采集进程未连接（可能已崩溃或正在重启），请稍后重试或重新连接设备。";
        }

        if (rawError.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action}失败：设备在规定时间内没有响应（后台进程可能繁忙或已断开），请稍后重试。";
        }

        if (rawError.Contains("invalid response", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action}失败：与后台采集进程通信异常，请重试或重启程序。";
        }

        // Fall back to the raw device/worker error so the real reason is never hidden.
        return string.IsNullOrWhiteSpace(rawError)
            ? $"{action}失败。"
            : $"{action}失败：{rawError}";
    }

    private void OnSnapshotUpdated(SnapshotModel snapshot)
    {
        AppendShapeWavelengthHistory(snapshot);
        lock (_snapshotUiUpdateSync)
        {
            _pendingUiSnapshot = snapshot;
            if (_isSnapshotUiUpdatePending)
            {
                return;
            }

            _isSnapshotUiUpdatePending = true;
        }

        _ = Dispatcher.BeginInvoke(new Action(ProcessPendingSnapshotUpdate), DispatcherPriority.Background);
    }

    private void AppendShapeWavelengthHistory(SnapshotModel snapshot)
    {
        snapshot = AlignSnapshotSensorDataToLoadedProfile(snapshot);
        if (snapshot.SensorWavelengthsNm.Length == 0)
        {
            return;
        }

        lock (_shapeWavelengthHistorySync)
        {
            if (!_shapeWavelengthHistoryByChannel.TryGetValue(snapshot.Channel, out Queue<float[]>? history))
            {
                history = new Queue<float[]>();
                _shapeWavelengthHistoryByChannel[snapshot.Channel] = history;
            }

            history.Enqueue(snapshot.SensorWavelengthsNm.ToArray());
            int limit = Math.Max(ShapeBaselineAverageFrameCount, ShapeRealtimeMedianFrameCount) * 2;
            while (history.Count > limit)
            {
                history.Dequeue();
            }
        }
    }

    private LoadedCoefficientProfile? ResolveLoadedCoefficientProfileForChannel(int channel)
    {
        if (_loadedCoefficientProfilesByChannel.TryGetValue(channel, out LoadedCoefficientProfile? cachedProfile))
        {
            return cachedProfile;
        }

        return _activeCoefficientChannel == channel ? _loadedCoefficientProfile : null;
    }

    private void ProcessPendingSnapshotUpdate()
    {
        SnapshotModel? snapshot;
        lock (_snapshotUiUpdateSync)
        {
            snapshot = _pendingUiSnapshot;
            _pendingUiSnapshot = null;
            _isSnapshotUiUpdatePending = false;
        }

        if (snapshot is null)
        {
            return;
        }

        snapshot = NormalizeSnapshotForDisplay(snapshot);
        _lastSnapshot = snapshot;
        _snapshotsByChannel[snapshot.Channel] = snapshot;
        AppendSingleSensorTrend(snapshot);
        UpdateRealtimeFrequencyEstimate(snapshot);
        UpdateCurrentMonitorChannelDisplay();
        if (GetSelectedMonitorOption() is null)
        {
            SyncChannelSelections(snapshot.Channel);
        }
        SnapshotModel? selectedSnapshot = ResolveSelectedSnapshot();
        if (selectedSnapshot is not null && ShouldRefreshSensorList(selectedSnapshot))
        {
            RefreshSensorOptions(selectedSnapshot);
        }
        else if (selectedSnapshot is null && ShouldRefreshSensorList(null))
        {
            RefreshSensorOptionsFromCoefficientProfile(preserveScroll: true, ensureSelectedRowVisible: false);
        }
        CurrentAlarmCountText.Text = AlarmFeaturesEnabled
            ? ((_currentDeviceView?.CurrentAlarmCount) ?? snapshot.Alarms.Length).ToString(CultureInfo.InvariantCulture)
            : "0";
        if (!AlarmFeaturesEnabled && _realtimeAlarmRows.Count > 0)
        {
            _realtimeAlarmRows.Clear();
        }
        WaveTimeTextBlock.Text = snapshot.Timestamp.ToString("yyyy-MM-dd\nHH:mm:ss", CultureInfo.InvariantCulture);
        UpdateRealtimeFrequencyDisplay();
        RedrawSelectedChannelViews();
    }

    private SnapshotModel ApplyShapeWavelengthMedianFilter(SnapshotModel snapshot)
    {
        if (!TryGetMedianFilteredShapeWavelengths(snapshot.Channel, snapshot.SensorWavelengthsNm.Length, ShapeRealtimeMedianFrameCount, out float[] filtered))
        {
            return snapshot;
        }

        return CloneSnapshotWithSensorWavelengths(snapshot, filtered);
    }

    private bool TryGetMedianFilteredShapeWavelengths(int channel, int wavelengthCount, int frameCount, out float[] filtered)
    {
        filtered = Array.Empty<float>();
        if (wavelengthCount <= 0 || frameCount <= 1)
        {
            return false;
        }

        float[][] frames;
        lock (_shapeWavelengthHistorySync)
        {
            if (!_shapeWavelengthHistoryByChannel.TryGetValue(channel, out Queue<float[]>? history) || history.Count < 2)
            {
                return false;
            }

            frames = history
                .Where(x => x.Length >= wavelengthCount)
                .TakeLast(frameCount)
                .ToArray();
        }

        if (frames.Length < 2)
        {
            return false;
        }

        filtered = new float[wavelengthCount];
        for (int i = 0; i < wavelengthCount; i++)
        {
            float[] values = frames
                .Select(frame => frame[i])
                .Where(v => float.IsFinite(v) && v > 0)
                .OrderBy(v => v)
                .ToArray();
            if (values.Length == 0)
            {
                filtered[i] = float.NaN;
                continue;
            }

            int mid = values.Length / 2;
            filtered[i] = values.Length % 2 == 1
                ? values[mid]
                : 0.5f * (values[mid - 1] + values[mid]);
        }

        return true;
    }

    private bool TryGetAveragedShapeBaseline(
        int channel,
        int wavelengthCount,
        out float[] averageWavelengths,
        out int frameCount,
        out float maxStdPm)
    {
        averageWavelengths = Array.Empty<float>();
        frameCount = 0;
        maxStdPm = 0f;
        if (wavelengthCount <= 0)
        {
            return false;
        }

        float[][] frames;
        lock (_shapeWavelengthHistorySync)
        {
            if (!_shapeWavelengthHistoryByChannel.TryGetValue(channel, out Queue<float[]>? history))
            {
                return false;
            }

            frames = history
                .Where(x => x.Length >= wavelengthCount)
                .TakeLast(ShapeBaselineAverageFrameCount)
                .ToArray();
        }

        frameCount = frames.Length;
        if (frameCount < ShapeBaselineMinimumFrameCount)
        {
            return false;
        }

        averageWavelengths = new float[wavelengthCount];
        for (int i = 0; i < wavelengthCount; i++)
        {
            float[] values = frames
                .Select(frame => frame[i])
                .Where(v => float.IsFinite(v) && v > 0)
                .ToArray();
            if (values.Length == 0)
            {
                averageWavelengths[i] = float.NaN;
                continue;
            }

            float mean = values.Average();
            averageWavelengths[i] = mean;
            if (values.Length > 1)
            {
                float variance = values.Select(v => (v - mean) * (v - mean)).Average();
                maxStdPm = Math.Max(maxStdPm, MathF.Sqrt(variance) * 1000f);
            }
        }

        return true;
    }

    private static SnapshotModel CloneSnapshotWithSensorWavelengths(SnapshotModel snapshot, float[] wavelengths) => new()
    {
        Timestamp = snapshot.Timestamp,
        TimestampMs = snapshot.TimestampMs,
        Channel = snapshot.Channel,
        PositionsM = snapshot.PositionsM,
        TemperaturesC = snapshot.TemperaturesC,
        SensorPositionsM = snapshot.SensorPositionsM,
        SensorTemperaturesC = snapshot.SensorTemperaturesC,
        SensorWavelengthsNm = wavelengths,
        SpectrumXAxisNm = snapshot.SpectrumXAxisNm,
        SpectrumValues = snapshot.SpectrumValues,
        SpectrumSensorIndex = snapshot.SpectrumSensorIndex,
        SpectrumSensorPositionM = snapshot.SpectrumSensorPositionM,
        SpectrumSensorWavelengthNm = snapshot.SpectrumSensorWavelengthNm,
        SpectrumSensorTemperatureC = snapshot.SpectrumSensorTemperatureC,
        Alarms = snapshot.Alarms,
        MinTemp = snapshot.MinTemp,
        MaxTemp = snapshot.MaxTemp,
        AvgTemp = snapshot.AvgTemp,
        MaxPosM = snapshot.MaxPosM,
        StatusOk = snapshot.StatusOk
    };

    private bool ShouldRefreshSensorList(SnapshotModel? snapshot)
    {
        DateTime nowUtc = DateTime.UtcNow;
        int channel = snapshot?.Channel ?? GetSelectedMonitorChannelIndex();
        int count = snapshot?.SensorWavelengthsNm.Length ?? _loadedCoefficientProfile?.DisplaySensorPositionsM.Length ?? 0;
        if (channel != _lastSensorListRefreshChannel ||
            count != _lastSensorListRefreshCount ||
            nowUtc - _lastSensorListRefreshUtc >= SensorListRefreshInterval)
        {
            _lastSensorListRefreshUtc = nowUtc;
            _lastSensorListRefreshChannel = channel;
            _lastSensorListRefreshCount = count;
            return true;
        }

        return false;
    }

    private SnapshotModel NormalizeSnapshotForDisplay(SnapshotModel snapshot)
    {
        LoadedCoefficientProfile? profile = ResolveLoadedCoefficientProfileForChannel(snapshot.Channel);
        if (profile is null)
        {
            return snapshot;
        }

        snapshot = AlignSnapshotSensorDataToProfile(snapshot, profile);
        return RecalculateSnapshotTemperaturesFromCoefficients(snapshot, profile);
    }

    private void OnAlarmRaised(AlarmRecord alarm)
    {
        if (!AlarmFeaturesEnabled)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _realtimeAlarmRows.Insert(0, new RealtimeAlarmRow
            {
                Seq = 1,
                TimeText = alarm.TimeText,
                ChannelIndex = alarm.Channel,
                ChannelText = alarm.ChannelText,
                SensorIndex = alarm.SensorIndex,
                TypeText = alarm.TypeText,
                PositionM = alarm.PositionM
            });
            if (_realtimeAlarmRows.Count > 200)
            {
                _realtimeAlarmRows.RemoveAt(_realtimeAlarmRows.Count - 1);
            }
            ReindexRealtimeRows();
            PersistRealtimeAlarmRowsForCurrentDevice();
            UpdateStatusCardsFromViewState(_currentDeviceView ?? _service?.ViewState);
        });
    }

    private void ResetRealtimeAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        _service?.ResetCurrentAlarms();
        _realtimeAlarmRows.Clear();
        PersistRealtimeAlarmRowsForCurrentDevice();
        UpdateStatusCardsFromViewState(_currentDeviceView ?? _service?.ViewState);
    }

    private void ChannelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingGraphSelection)
        {
            return;
        }

        if (ChannelListBox.SelectedItem is not ChannelOption option)
        {
            return;
        }

        if (option.ChannelIndex == _selectedMonitorChannel)
        {
            return;
        }

        ChannelOption? activeOption = FindChannelOption(_selectedMonitorChannel);
        if (activeOption is null)
        {
            ActivateMonitorChannel(option);
            return;
        }

        _isSynchronizingGraphSelection = true;
        try
        {
            ChannelListBox.SelectedItem = activeOption;
        }
        finally
        {
            _isSynchronizingGraphSelection = false;
        }
    }

    private void ChannelListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TryGetChannelOptionFromSource(e.OriginalSource as DependencyObject) is not ChannelOption option)
        {
            return;
        }

        ActivateMonitorChannel(option);
        e.Handled = true;
    }

    private void SensorInfoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingGraphSelection)
        {
            return;
        }

        if (SensorInfoGrid.SelectedItem is not SensorInfoRow row)
        {
            return;
        }

        _service?.SetSpectrumSensorIndex(row.SensorIndex);
        if (_service is not null)
        {
            SnapshotModel? refreshed = _service.TryReadLatestSnapshotNow();
            if (refreshed is not null)
            {
                refreshed = NormalizeSnapshotForDisplay(refreshed);
                _lastSnapshot = refreshed;
                _snapshotsByChannel[refreshed.Channel] = refreshed;
                RefreshSensorOptions(refreshed);
            }
        }

        _sensorSpectrumViewport.Reset();
        RedrawSelectedChannelViews();
    }

    private void RealtimeAlarmGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RealtimeAlarmGrid.SelectedItem is not RealtimeAlarmRow alarmRow)
        {
            return;
        }

        ChannelOption? selectedOption = GetSelectedMonitorOption();
        bool isRunning = _service?.GetState() == 4;
        bool canJumpChannel = !isRunning;
        if (isRunning &&
            (selectedOption?.IsAllChannels == true ||
             selectedOption?.ChannelIndex != alarmRow.ChannelIndex))
        {
            AppMessageDialog.ShowInfo(this, "报警定位", "运行中不能切换监控通道，请先停止运行后再跳转到该报警通道。");
            e.Handled = true;
            return;
        }

        if (canJumpChannel)
        {
            ChannelOption? option = FindChannelOption(alarmRow.ChannelIndex);
            if (option is null)
            {
                return;
            }

            ActivateMonitorChannel(option, preserveScroll: false, ensureSelectedRowVisible: true, resetViewports: false);
        }

        SensorInfoRow? sensorRow = _sensorInfoRows.FirstOrDefault(x => x.SensorIndex == alarmRow.SensorIndex);
        if (sensorRow is not null)
        {
            SetSelectedSensorRow(sensorRow, ensureVisible: true);
            _service?.SetSpectrumSensorIndex(sensorRow.SensorIndex);
            RedrawSelectedChannelViews();
        }

        e.Handled = true;
    }

    private void GraphViewTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, GraphViewTabControl) || !ReferenceEquals(e.OriginalSource, GraphViewTabControl))
        {
            return;
        }

        if (GraphViewTabControl.SelectedIndex == 2)
        {
            _lastShapeSensingTimestampMs = -1;
            _lastShapeSensingRefreshUtc = DateTime.MinValue;
        }

        ApplyCurrentGraphViewState(redrawCharts: true);
        SaveUiState();
    }

    private void ShowWavelengthViewButton_Click(object sender, RoutedEventArgs e)
    {
        SetTemperatureAxisRangePanelOpen(false);
        GraphViewTabControl.SelectedIndex = 0;
    }

    private void ShowTemperatureViewButton_Click(object sender, RoutedEventArgs e)
    {
        GraphViewTabControl.SelectedIndex = 1;
    }

    private void OpenShapeMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        SetTemperatureAxisRangePanelOpen(false);
        UpdateShapeReconstructionTitle();
        _lastShapeSensingTimestampMs = -1;
        _lastShapeSensingRefreshUtc = DateTime.MinValue;
        GraphViewTabControl.SelectedIndex = 2;
        RedrawSelectedChannelViews();
    }

    private void ShapeSensingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || GraphViewTabControl is null || ShapeStatusTextBlock is null)
        {
            return;
        }

        UpdateShapeReconstructionTitle();
        _lastShapeSensingTimestampMs = -1;
        _lastShapeSensingRefreshUtc = DateTime.MinValue;
        _shapeReconstructionViewport.Reset();
        _shapeReconstructionZoomViewport.Reset();
        RedrawSelectedChannelViews();
    }

    private ShapeSensingMode GetSelectedShapeSensingMode()
    {
        if (ShapeSensingModeComboBox?.SelectedItem is ComboBoxItem item &&
            string.Equals(item.Tag?.ToString(), nameof(ShapeSensingMode.DualFiber), StringComparison.Ordinal))
        {
            return ShapeSensingMode.DualFiber;
        }

        return ShapeSensingMode.SingleFiber;
    }

    private void UpdateShapeReconstructionTitle()
    {
        if (ShapeReconstructionTitleTextBlock is null)
        {
            return;
        }

        ShapeReconstructionTitleTextBlock.Text = GetSelectedShapeSensingMode() == ShapeSensingMode.DualFiber
            ? "二维形状重构"
            : "相对基准形状估计";
    }

    private ShapeSensingProfile? BuildShapeSensingProfile(int channel)
    {
        LoadedCoefficientProfile? profile = null;
        if (_loadedCoefficientProfilesByChannel.TryGetValue(channel, out LoadedCoefficientProfile? cachedProfile))
        {
            profile = cachedProfile;
        }
        else if (_activeCoefficientChannel == channel && _loadedCoefficientProfile is not null)
        {
            profile = _loadedCoefficientProfile;
        }

        if (profile is null)
        {
            return null;
        }

        return new ShapeSensingProfile
        {
            Channel = channel,
            SensorPositionsM = profile.DisplaySensorPositionsM.ToArray(),
            StrainSensitivity = profile.StrainSensitivity.ToArray(),
            ReferenceStrainWavelengthsNm = profile.ReferenceStrainWavelengthsNm.ToArray()
        };
    }

    private void InitializeMonitorChannelOptions()
    {
        _monitorChannelOptions.Clear();
        _channelOptions.Clear();
        _monitorChannelOptions.Add(ChannelOption.CreateAllChannels());
        for (int i = 0; i < MaxMonitorChannels; i++)
        {
            _monitorChannelOptions.Add(new ChannelOption(i));
            _channelOptions.Add(new ChannelOption(i));
        }
    }

    private void InitializeParameterChannelSettings()
    {
        _parameterChannelSettings.Clear();
        for (int i = 0; i < MaxMonitorChannels; i++)
        {
            _parameterChannelSettings.Add(new ParameterChannelSettingItem(i)
            {
                IsEnabled = false,
                CenterWavelengthText = string.Empty
            });
        }
    }

    private ParameterChannelSettingItem GetOrCreateParameterChannelSetting(int channelIndex)
    {
        ParameterChannelSettingItem? existing = _parameterChannelSettings.FirstOrDefault(x => x.ChannelIndex == channelIndex);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ParameterChannelSettingItem(channelIndex)
        {
            IsEnabled = false,
            CenterWavelengthText = string.Empty
        };

        int insertIndex = 0;
        while (insertIndex < _parameterChannelSettings.Count &&
               _parameterChannelSettings[insertIndex].ChannelIndex < channelIndex)
        {
            insertIndex++;
        }

        _parameterChannelSettings.Insert(insertIndex, created);
        return created;
    }

    private void EnsureChannelOption(int channelIndex)
    {
        if (_channelOptions.Any(x => !x.IsAllChannels && x.ChannelIndex == channelIndex))
        {
            return;
        }

        _monitorChannelOptions.Add(new ChannelOption(channelIndex));
        _channelOptions.Add(new ChannelOption(channelIndex));
        var orderedMonitor = _monitorChannelOptions
            .OrderBy(x => x.IsAllChannels ? -1 : x.ChannelIndex)
            .ToList();
        _monitorChannelOptions.Clear();
        foreach (ChannelOption item in orderedMonitor)
        {
            _monitorChannelOptions.Add(item);
        }

        var ordered = _channelOptions.OrderBy(x => x.ChannelIndex).ToList();
        _channelOptions.Clear();
        foreach (ChannelOption item in ordered)
        {
            _channelOptions.Add(item);
        }
    }

    private void SyncChannelSelections(int preferredChannelIndex)
    {
        ChannelOption? selected = _monitorAllChannels
            ? FindChannelOption(-1)
            : FindChannelOption(_selectedMonitorChannel);

        selected ??= _monitorChannelOptions.FirstOrDefault(x => !x.IsAllChannels && x.ChannelIndex == preferredChannelIndex);

        if (selected is null)
        {
            selected = _monitorChannelOptions.FirstOrDefault(x => !x.IsAllChannels) ??
                       _monitorChannelOptions.FirstOrDefault();
        }

        if (selected is null)
        {
            return;
        }

        SetSelectedChannelControls(selected);
    }

    private ChannelOption? FindChannelOption(int channelIndex)
    {
        return _monitorChannelOptions.FirstOrDefault(x => x.ChannelIndex == channelIndex);
    }

    private ChannelOption? TryGetChannelOptionFromSource(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is ChannelOption option)
            {
                return option;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return ChannelListBox.SelectedItem as ChannelOption;
    }

    private void ActivateMonitorChannel(
        ChannelOption option,
        bool preserveScroll = false,
        bool ensureSelectedRowVisible = true,
        bool resetViewports = true)
    {
        SetSelectedChannelControls(option);
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        RefreshSelectedChannelData(preserveScroll, ensureSelectedRowVisible);
        if (resetViewports)
        {
            ResetChartViewports();
        }

        RedrawSelectedChannelViews();
        SaveUiState();
    }

    private ChannelOption? GetSelectedMonitorOption()
    {
        if (ChannelListBox.SelectedItem is ChannelOption selectedItem)
        {
            return selectedItem;
        }

        if (_monitorAllChannels)
        {
            return FindChannelOption(-1);
        }

        if (_selectedMonitorChannel >= 0)
        {
            return FindChannelOption(_selectedMonitorChannel);
        }

        return null;
    }

    private void UpdateCurrentMonitorChannelDisplay()
    {
        if (CurrentMonitorChannelTextBlock is null)
        {
            return;
        }

        int channel = _lastSnapshot?.Channel ?? GetSelectedMonitorChannelIndex();
        if (channel < 0)
        {
            channel = 0;
        }

        CurrentMonitorChannelTextBlock.Text = $"当前通道：{FormatChannelLabel(channel)}";
    }

    private int GetSelectedMonitorChannelIndex(int fallback = 0)
    {
        if (GetSelectedMonitorOption() is ChannelOption option)
        {
            if (!option.IsAllChannels)
            {
                return option.ChannelIndex;
            }
        }

        if (_selectedMonitorChannel >= 0)
        {
            return _selectedMonitorChannel;
        }

        if (_lastSnapshot is not null)
        {
            return _lastSnapshot.Channel;
        }

        if (_activeCoefficientChannel >= 0)
        {
            return _activeCoefficientChannel;
        }

        return Math.Clamp(fallback, 0, MaxMonitorChannels - 1);
    }

    private void ClearSensorOptions()
    {
        _sensorInfoRows.Clear();
        SetSelectedSensorRow(null);
    }

    private void RefreshSelectedChannelData(bool preserveScroll, bool ensureSelectedRowVisible)
    {
        SnapshotModel? snapshot = ResolveSelectedSnapshot();
        if (snapshot is not null)
        {
            RefreshSensorOptions(snapshot, preserveScroll, ensureSelectedRowVisible);
        }
        else if (!RefreshSensorOptionsFromCoefficientProfile(preserveScroll, ensureSelectedRowVisible))
        {
            ClearSensorOptions();
        }

        if (_calibrationWindow is not null)
        {
            _calibrationWindow.UpdateParameters(BuildCalibrationWindowParameters(GetSelectedMonitorChannelIndex()));
        }
    }

    private bool RefreshSensorOptionsFromCoefficientProfile(bool preserveScroll, bool ensureSelectedRowVisible)
    {
        if (_loadedCoefficientProfile is null)
        {
            return false;
        }

        RefreshSensorOptions(_loadedCoefficientProfile, preserveScroll, ensureSelectedRowVisible);
        return true;
    }

    private void RefreshSensorOptions(
        SnapshotModel snapshot,
        bool preserveScroll = true,
        bool ensureSelectedRowVisible = false)
    {
        (double HorizontalOffset, double VerticalOffset)? scrollOffsets =
            preserveScroll ? CaptureDataGridScrollOffsets(SensorInfoGrid) : null;
        SensorInfoDisplayMode displayMode = GetSensorInfoDisplayMode();
        int previousSensorIndex =
            (SensorInfoGrid.SelectedItem as SensorInfoRow)?.SensorIndex ??
            snapshot.SpectrumSensorIndex;
        BuildDisplaySensorSeries(snapshot, out int[] rawSensorIndexes, out float[] positions, out float[] wavelengths, out float[] temperatures);
        float[] strains = displayMode == SensorInfoDisplayMode.Strain
            ? ResolveSensorInfoStrainValues(snapshot.Channel, ref rawSensorIndexes, ref positions, ref wavelengths, ref temperatures)
            : Array.Empty<float>();

        _sensorInfoRows.Clear();
        for (int i = 0; i < positions.Length; i++)
        {
            float strain = i < strains.Length ? strains[i] : float.NaN;
            _sensorInfoRows.Add(new SensorInfoRow(rawSensorIndexes[i], positions[i], wavelengths[i], temperatures[i], strain, displayMode));
        }

        if (_sensorInfoRows.Count == 0)
        {
            SetSelectedSensorRow(null);
            return;
        }

        SensorInfoRow? target = _sensorInfoRows.FirstOrDefault(x => x.SensorIndex == previousSensorIndex);
        SetSelectedSensorRow(target ?? _sensorInfoRows[0], ensureSelectedRowVisible);
        if (preserveScroll && !ensureSelectedRowVisible)
        {
            RestoreDataGridScrollOffsets(SensorInfoGrid, scrollOffsets);
        }
    }

    private void RefreshSensorOptions(
        LoadedCoefficientProfile profile,
        bool preserveScroll = true,
        bool ensureSelectedRowVisible = false)
    {
        (double HorizontalOffset, double VerticalOffset)? scrollOffsets =
            preserveScroll ? CaptureDataGridScrollOffsets(SensorInfoGrid) : null;
        SensorInfoDisplayMode displayMode = GetSensorInfoDisplayMode();
        int previousSensorIndex =
            (SensorInfoGrid.SelectedItem as SensorInfoRow)?.SensorIndex ??
            0;

        _sensorInfoRows.Clear();
        int sensorCount = profile.DisplaySensorPositionsM.Length;
        for (int i = 0; i < sensorCount; i++)
        {
            float pos = profile.DisplaySensorPositionsM[i];
            float wavelength = float.NaN;
            float temperature = float.NaN;
            float strain = float.NaN;
            _sensorInfoRows.Add(new SensorInfoRow(i, pos, wavelength, temperature, strain, displayMode));
        }

        if (_sensorInfoRows.Count == 0)
        {
            SetSelectedSensorRow(null);
            return;
        }

        SensorInfoRow? target = _sensorInfoRows.FirstOrDefault(x => x.SensorIndex == previousSensorIndex);
        SetSelectedSensorRow(target ?? _sensorInfoRows[0], ensureSelectedRowVisible);
        if (preserveScroll && !ensureSelectedRowVisible)
        {
            RestoreDataGridScrollOffsets(SensorInfoGrid, scrollOffsets);
        }
    }

    private void BuildDisplaySensorSeries(
        SnapshotModel snapshot,
        out int[] rawSensorIndexes,
        out float[] positions,
        out float[] wavelengths,
        out float[] temperatures)
    {
        LoadedCoefficientProfile? profile = ResolveLoadedCoefficientProfileForChannel(snapshot.Channel);

        float[] rawTemps = snapshot.SensorTemperaturesC.Length > 0 ? snapshot.SensorTemperaturesC : snapshot.TemperaturesC;
        if (profile is not null && profile.DisplaySensorPositionsM.Length > 0)
        {
            snapshot = AlignSnapshotSensorDataToProfile(snapshot, profile);
            int count = profile.DisplaySensorPositionsM.Length;
            rawSensorIndexes = Enumerable.Range(0, count).ToArray();
            positions = profile.DisplaySensorPositionsM.ToArray();
            wavelengths = new float[count];
            temperatures = new float[count];

            for (int i = 0; i < count; i++)
            {
                wavelengths[i] = i < snapshot.SensorWavelengthsNm.Length
                    ? snapshot.SensorWavelengthsNm[i]
                    : float.NaN;
                temperatures[i] = TryResolveDisplayTemperatureFromProfile(profile, i, wavelengths[i]);
            }

            return;
        }

        positions = snapshot.SensorPositionsM.Length > 0 ? snapshot.SensorPositionsM.ToArray() : snapshot.PositionsM.ToArray();
        wavelengths = snapshot.SensorWavelengthsNm.Length > 0 ? snapshot.SensorWavelengthsNm.ToArray() : Array.Empty<float>();
        temperatures = rawTemps.ToArray();
        rawSensorIndexes = Enumerable.Range(0, positions.Length).ToArray();
    }

    private SnapshotModel AlignSnapshotSensorDataToLoadedProfile(SnapshotModel snapshot)
    {
        LoadedCoefficientProfile? profile = ResolveLoadedCoefficientProfileForChannel(snapshot.Channel);
        return AlignSnapshotSensorDataToProfile(snapshot, profile);
    }

    private static SnapshotModel AlignSnapshotSensorDataToProfile(SnapshotModel snapshot, LoadedCoefficientProfile? profile)
    {
        if (profile is null ||
            profile.DisplaySensorPositionsM.Length == 0 ||
            snapshot.SensorWavelengthsNm.Length == 0)
        {
            return snapshot;
        }

        int targetCount = profile.DisplaySensorPositionsM.Length;
        float[] alignedWavelengths = Enumerable.Repeat(float.NaN, targetCount).ToArray();
        float[] alignedTemperatures = Enumerable.Repeat(float.NaN, targetCount).ToArray();
        float[] alignedPositions = profile.DisplaySensorPositionsM.ToArray();
        bool[] used = new bool[snapshot.SensorWavelengthsNm.Length];

        for (int profileIndex = 0; profileIndex < targetCount; profileIndex++)
        {
            int sourceIndex = FindBestSnapshotSensorMatch(snapshot, profile, profileIndex, used);
            if (sourceIndex < 0)
            {
                continue;
            }

            used[sourceIndex] = true;
            float wavelength = snapshot.SensorWavelengthsNm[sourceIndex];
            alignedWavelengths[profileIndex] = wavelength;
            alignedTemperatures[profileIndex] = TryResolveDisplayTemperatureFromProfile(profile, profileIndex, wavelength);
        }

        return CloneSnapshotWithSensorData(snapshot, alignedPositions, alignedTemperatures, alignedWavelengths);
    }

    private static int FindBestSnapshotSensorMatch(
        SnapshotModel snapshot,
        LoadedCoefficientProfile profile,
        int profileIndex,
        bool[] used)
    {
        float targetPosition = profileIndex < profile.DisplaySensorPositionsM.Length
            ? profile.DisplaySensorPositionsM[profileIndex]
            : float.NaN;
        float expectedWavelength = ResolveProfileReferenceWavelength(profile, profileIndex);
        float[] sourcePositions = snapshot.SensorPositionsM.Length > 0 ? snapshot.SensorPositionsM : snapshot.PositionsM;

        int bestIndex = -1;
        float bestScore = float.PositiveInfinity;
        for (int sourceIndex = 0; sourceIndex < snapshot.SensorWavelengthsNm.Length; sourceIndex++)
        {
            if (sourceIndex < used.Length && used[sourceIndex])
            {
                continue;
            }

            float wavelength = snapshot.SensorWavelengthsNm[sourceIndex];
            if (!float.IsFinite(wavelength) || wavelength <= 0)
            {
                continue;
            }

            float score = 0f;
            if (float.IsFinite(expectedWavelength) && expectedWavelength > 0)
            {
                float wavelengthDiffNm = Math.Abs(wavelength - expectedWavelength);
                score += wavelengthDiffNm * 100f;
            }
            else
            {
                score += sourceIndex == profileIndex ? 0f : 50f;
            }

            if (sourceIndex < sourcePositions.Length && float.IsFinite(sourcePositions[sourceIndex]) && float.IsFinite(targetPosition))
            {
                score += Math.Abs(sourcePositions[sourceIndex] - targetPosition) * 2f;
            }
            else
            {
                score += Math.Abs(sourceIndex - profileIndex) * 0.25f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = sourceIndex;
            }
        }

        return bestIndex;
    }

    private static float ResolveProfileReferenceWavelength(LoadedCoefficientProfile profile, int sensorIndex)
    {
        if (sensorIndex >= 0 &&
            sensorIndex < profile.ReferenceWavelengthsNm.Length &&
            float.IsFinite(profile.ReferenceWavelengthsNm[sensorIndex]) &&
            profile.ReferenceWavelengthsNm[sensorIndex] > 0)
        {
            return profile.ReferenceWavelengthsNm[sensorIndex];
        }

        if (sensorIndex >= 0 &&
            sensorIndex < profile.ReferenceStrainWavelengthsNm.Length &&
            float.IsFinite(profile.ReferenceStrainWavelengthsNm[sensorIndex]) &&
            profile.ReferenceStrainWavelengthsNm[sensorIndex] > 0)
        {
            return profile.ReferenceStrainWavelengthsNm[sensorIndex];
        }

        return float.NaN;
    }

    private static SnapshotModel CloneSnapshotWithSensorData(
        SnapshotModel snapshot,
        float[] sensorPositions,
        float[] sensorTemperatures,
        float[] sensorWavelengths) => new()
    {
        Timestamp = snapshot.Timestamp,
        TimestampMs = snapshot.TimestampMs,
        Channel = snapshot.Channel,
        PositionsM = snapshot.PositionsM,
        TemperaturesC = snapshot.TemperaturesC,
        SensorPositionsM = sensorPositions,
        SensorTemperaturesC = sensorTemperatures,
        SensorWavelengthsNm = sensorWavelengths,
        SpectrumXAxisNm = snapshot.SpectrumXAxisNm,
        SpectrumValues = snapshot.SpectrumValues,
        SpectrumSensorIndex = snapshot.SpectrumSensorIndex,
        SpectrumSensorPositionM = snapshot.SpectrumSensorPositionM,
        SpectrumSensorWavelengthNm = snapshot.SpectrumSensorWavelengthNm,
        SpectrumSensorTemperatureC = snapshot.SpectrumSensorTemperatureC,
        Alarms = snapshot.Alarms,
        MinTemp = snapshot.MinTemp,
        MaxTemp = snapshot.MaxTemp,
        AvgTemp = snapshot.AvgTemp,
        MaxPosM = snapshot.MaxPosM,
        StatusOk = snapshot.StatusOk
    };

    private static float TryResolveDisplayTemperatureFromProfile(SnapshotModel snapshot, LoadedCoefficientProfile profile, int sensorIndex)
    {
        if (sensorIndex < 0 ||
            sensorIndex >= snapshot.SensorWavelengthsNm.Length ||
            sensorIndex >= profile.TempSensitivityPmPerC.Length ||
            sensorIndex >= profile.ReferenceTemperaturesC.Length ||
            sensorIndex >= profile.ReferenceWavelengthsNm.Length)
        {
            return float.NaN;
        }

        float wavelength = snapshot.SensorWavelengthsNm[sensorIndex];
        float sensitivityPm = profile.TempSensitivityPmPerC[sensorIndex];
        float referenceTemperature = profile.ReferenceTemperaturesC[sensorIndex];
        float referenceWavelength = profile.ReferenceWavelengthsNm[sensorIndex];

        if (!float.IsFinite(wavelength) ||
            !float.IsFinite(sensitivityPm) ||
            Math.Abs(sensitivityPm) <= 0.0001f ||
            !float.IsFinite(referenceTemperature) ||
            !float.IsFinite(referenceWavelength) ||
            referenceWavelength <= 0)
        {
            return float.NaN;
        }

        return referenceTemperature + ((wavelength - referenceWavelength) * 1000f / sensitivityPm);
    }

    private static float TryResolveDisplayTemperatureFromProfile(LoadedCoefficientProfile profile, int sensorIndex, float wavelength)
    {
        if (sensorIndex < 0 ||
            sensorIndex >= profile.TempSensitivityPmPerC.Length ||
            sensorIndex >= profile.ReferenceTemperaturesC.Length ||
            sensorIndex >= profile.ReferenceWavelengthsNm.Length)
        {
            return float.NaN;
        }

        float sensitivityPm = profile.TempSensitivityPmPerC[sensorIndex];
        float referenceTemperature = profile.ReferenceTemperaturesC[sensorIndex];
        float referenceWavelength = profile.ReferenceWavelengthsNm[sensorIndex];

        if (!float.IsFinite(wavelength) ||
            !float.IsFinite(sensitivityPm) ||
            Math.Abs(sensitivityPm) <= 0.0001f ||
            !float.IsFinite(referenceTemperature) ||
            !float.IsFinite(referenceWavelength) ||
            referenceWavelength <= 0)
        {
            return float.NaN;
        }

        return referenceTemperature + ((wavelength - referenceWavelength) * 1000f / sensitivityPm);
    }


    private static bool TryGetDisplaySensorValue(int rawSensorIndex, int[] rawSensorIndexes, float[] values, out float value)
    {
        for (int i = 0; i < rawSensorIndexes.Length && i < values.Length; i++)
        {
            if (rawSensorIndexes[i] == rawSensorIndex)
            {
                value = values[i];
                return true;
            }
        }

        value = float.NaN;
        return false;
    }

    private void RedrawSelectedChannelViews()
    {
        SnapshotModel? snapshot = ResolveSelectedSnapshot();
        if (snapshot is null)
        {
            SensorSpectrumCanvas.Children.Clear();
            SpectrumCanvas.Children.Clear();
            SingleSensorWavelengthCanvas.Children.Clear();
            SingleSensorTemperatureCanvas.Children.Clear();
            WaveformCanvas.Children.Clear();
            SingleSensorStrainCanvas.Children.Clear();
            StrainArrayCanvas.Children.Clear();
            ShapeReconstructionCanvas.Children.Clear();
            ShapeStatusTextBlock.Text = "等待实时波长数据";
            return;
        }

        int selectedGraphIndex = GraphViewTabControl?.SelectedIndex ?? 0;
        if (selectedGraphIndex == 0)
        {
            BuildDisplaySensorSeries(snapshot, out int[] rawSensorIndexes, out float[] arrayPositions, out float[] arrayWavelengths, out _);
            DrawSensorSpectrum(snapshot.SpectrumXAxisNm, snapshot.SpectrumValues);
            DrawWavelengthArray(arrayPositions, arrayWavelengths);
            DrawSingleSensorWavelengthTrend(snapshot, rawSensorIndexes, arrayWavelengths);
            return;
        }

        if (selectedGraphIndex == 1)
        {
            BuildDisplaySensorSeries(snapshot, out int[] rawSensorIndexes, out float[] arrayPositions, out _, out float[] arrayTemps);
            DrawSingleSensorTemperatureTrend(snapshot, rawSensorIndexes, arrayTemps);
            DrawTemperatureWaveform(arrayPositions, arrayTemps);
            return;
        }

        if (selectedGraphIndex == 2)
        {
            DrawShapeSensingViews(snapshot);
        }
    }

    private SnapshotModel? ResolveSelectedSnapshot()
    {
        if (GetSelectedMonitorOption() is ChannelOption option)
        {
            if (option.IsAllChannels)
            {
                return _lastSnapshot;
            }

            if (_snapshotsByChannel.TryGetValue(option.ChannelIndex, out SnapshotModel? selectedSnapshot))
            {
                return selectedSnapshot;
            }

            return null;
        }

        return _lastSnapshot;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await TryConnectDeviceAsync(autoRunAfterConnect: false, showErrorMessage: true);
    }

    private async void DeviceSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingDevice || DeviceSelectorComboBox.SelectedItem is not DeviceDefinition device)
        {
            return;
        }

        if (!device.Enabled)
        {
            if (_currentDevice is not null)
            {
                DeviceSelectorComboBox.SelectedItem = _currentDevice;
            }
            return;
        }

        await SwitchToDeviceAsync(device, attemptAutoConnect: device.AutoConnect);
    }

    private void OpenDeviceManagerDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceManagerGrid is not null)
        {
            DeviceManagerGrid.ItemsSource = _devices;
            DeviceManagerGrid.Items.Refresh();
        }

        UpdateEnableAllDevicesCheckBoxState();

        if (DeviceManagerDialogOverlay is not null)
        {
            DeviceManagerDialogOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CloseDeviceManagerDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceManagerDialogOverlay is not null)
        {
            DeviceManagerDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceDefinition device = CreateNewDeviceDefinition();
        _devices.Add(device);
        DeviceManagerGrid?.Items.Refresh();
        DeviceSelectorComboBox.Items.Refresh();
        UpdateEnableAllDevicesCheckBoxState();
        if (DeviceManagerGrid is not null)
        {
            DeviceManagerGrid.SelectedItem = device;
            DeviceManagerGrid.ScrollIntoView(device);
        }
    }

    private void RemoveDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceManagerGrid?.SelectedItem is not DeviceDefinition device)
        {
            return;
        }

        if (!AppMessageDialog.ShowConfirm(this, "设备管理", $"确定删除设备“{device.Name}”吗？", "删除", "取消"))
        {
            return;
        }

        if (_deviceSessions.TryGetValue(device.DeviceId, out DeviceSessionProxy? session))
        {
            session.Dispose();
            _deviceSessions.Remove(device.DeviceId);
        }
        _realtimeAlarmRowsByDeviceId.Remove(device.DeviceId);
        _runtimeCacheByDeviceId.Remove(device.DeviceId);

        bool wasCurrent = _currentDevice?.DeviceId == device.DeviceId;
        _devices.Remove(device);
        DeviceManagerGrid?.Items.Refresh();
        DeviceSelectorComboBox.Items.Refresh();
        UpdateEnableAllDevicesCheckBoxState();
        _deviceRegistry?.Save(_devices);

        if (wasCurrent)
        {
            DeviceDefinition? fallbackDevice = _devices.FirstOrDefault(x => x.Enabled);
            if (fallbackDevice is not null)
            {
                DeviceSelectorComboBox.SelectedItem = fallbackDevice;
            }
            else
            {
                DetachCurrentService();
                _currentDevice = null;
                _uiStatePath = string.Empty;
                ClearRuntimeStateForDeviceSwitch();
                RefreshConnectionState();
            }
        }
    }

    private async void SaveDeviceManagerButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceManagerGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
        DeviceManagerGrid?.CommitEdit(DataGridEditingUnit.Row, true);

        var disabledDeviceIds = new List<string>();
        foreach (DeviceDefinition device in _devices)
        {
            device.Name = string.IsNullOrWhiteSpace(device.Name) ? "设备" : device.Name.Trim();
            device.Ip = device.Ip?.Trim() ?? string.Empty;
            device.LastModifiedUtc = DateTime.UtcNow;
            if (!device.Enabled)
            {
                disabledDeviceIds.Add(device.DeviceId);
            }
            if (_deviceSessions.TryGetValue(device.DeviceId, out DeviceSessionProxy? session))
            {
                session.ViewState.Name = device.Name;
                session.ViewState.Ip = device.Ip;
            }
        }

        foreach (string deviceId in disabledDeviceIds)
        {
            if (_deviceSessions.TryGetValue(deviceId, out DeviceSessionProxy? session))
            {
                session.Dispose();
                _deviceSessions.Remove(deviceId);
            }
        }

        _deviceRegistry?.Save(_devices);
        DeviceSelectorComboBox.Items.Refresh();
        DeviceManagerGrid?.Items.Refresh();
        UpdateEnableAllDevicesCheckBoxState();

        if (_currentDevice is not null && !_currentDevice.Enabled)
        {
            DeviceDefinition? fallbackDevice = _devices.FirstOrDefault(x => x.Enabled);
            if (fallbackDevice is not null)
            {
                DeviceSelectorComboBox.SelectedItem = fallbackDevice;
                await SwitchToDeviceAsync(fallbackDevice, attemptAutoConnect: fallbackDevice.AutoConnect);
            }
            else
            {
                DetachCurrentService();
                _currentDevice = null;
                _uiStatePath = string.Empty;
                ClearRuntimeStateForDeviceSwitch();
                RefreshConnectionState();
            }
        }
        else
        {
            RefreshConnectionState();
            if (_currentDevice is not null)
            {
                SaveUiState();
            }
        }

        AddRuntimeLog($"设备配置已保存，共 {_devices.Count} 台。");
        CloseDeviceManagerDialogButton_Click(sender, e);
    }

    private void EnableAllDevicesCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        SetAllDevicesEnabled(true);
    }

    private void EnableAllDevicesCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingEnableAllDevicesCheckBox)
        {
            return;
        }

        SetAllDevicesEnabled(false);
    }

    private void SetAllDevicesEnabled(bool enabled)
    {
        if (_isUpdatingEnableAllDevicesCheckBox)
        {
            return;
        }

        foreach (DeviceDefinition device in _devices)
        {
            device.Enabled = enabled;
            device.LastModifiedUtc = DateTime.UtcNow;
        }

        DeviceManagerGrid?.Items.Refresh();
        UpdateEnableAllDevicesCheckBoxState();
    }

    private void UpdateEnableAllDevicesCheckBoxState()
    {
        if (EnableAllDevicesCheckBox is null)
        {
            return;
        }

        _isUpdatingEnableAllDevicesCheckBox = true;
        try
        {
            EnableAllDevicesCheckBox.IsChecked = _devices.Count > 0 && _devices.All(x => x.Enabled);
        }
        finally
        {
            _isUpdatingEnableAllDevicesCheckBox = false;
        }
    }

    private DeviceDefinition CreateNewDeviceDefinition()
    {
        string root = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HG-FBG");
        Directory.CreateDirectory(root);
        string deviceId = Guid.NewGuid().ToString("N");
        int nextSequence = 1;
        foreach (DeviceDefinition device in _devices)
        {
            string name = device.Name?.Trim() ?? string.Empty;
            Match match = Regex.Match(name, @"^设备(\d+)$");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                value >= nextSequence)
            {
                nextSequence = value + 1;
            }
        }

        return new DeviceDefinition
        {
            DeviceId = deviceId,
            Name = $"设备{nextSequence}",
            Enabled = true,
            AutoConnect = false,
            DbPath = IoPath.Combine(root, $"hg_fbg_monitor_{deviceId}.db"),
            UiStatePath = IoPath.Combine(root, $"ui_state_{deviceId}.json"),
            WorkerPipeName = $"hg_fbg_worker_{deviceId}",
            CreatedUtc = DateTime.UtcNow,
            LastModifiedUtc = DateTime.UtcNow
        };
    }

    private async void AttemptStartupAutoConnect()
    {
        if (_service is null || _currentDevice?.Enabled != true)
        {
            return;
        }

        await TryConnectDeviceAsync(autoRunAfterConnect: false, showErrorMessage: false);
    }

    private async Task TryConnectDeviceAsync(bool autoRunAfterConnect, bool showErrorMessage)
    {
        if (_service is null)
        {
            return;
        }

        if (_currentDevice?.Enabled != true)
        {
            RefreshConnectionState();
            if (showErrorMessage)
            {
                AppMessageDialog.ShowInfo(this, "连接设备", "当前设备未启用。请先在设备管理中勾选“启用”。");
            }
            return;
        }

        string ip = _currentDevice?.Ip?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        if (_currentDeviceView?.WorkerState is WorkerState.Offline or WorkerState.Faulted)
        {
            try
            {
                await _service.StartAsync();
                RefreshConnectionState();
            }
            catch (Exception ex)
            {
                RefreshConnectionState();
                if (showErrorMessage)
                {
                    AppMessageDialog.ShowInfo(this, "启动错误", $"设备 worker 重启失败。\n\n{ex.Message}");
                }
                return;
            }
        }

        int currentState = _service.GetState();
        int currentConnect = _service.GetConnect();
        if (currentState != 0)
        {
            RefreshConnectionState();

            if (currentConnect == 1)
            {
                if (showErrorMessage)
                {
                    AddRuntimeLog("设备已处于连接状态，无需重复连接。");
                }
            }
            else
            {
                AddRuntimeLog($"设备当前处于{StateToText(currentState)}，不再重复发起初始化。");
            }
            return;
        }

        int rc = _service.Connect(ip, use1GInit: true);
        if (rc != 0)
        {
            RefreshConnectionState();
            if (showErrorMessage)
            {
                AppMessageDialog.ShowInfo(this, "连接设备", BuildFriendlyHardwareError("连接设备", rc));
            }
            AddRuntimeLog($"连接失败：rc={rc}");
            return;
        }

        InvalidateAppliedHardwareConfigCache();
        RefreshConnectionState();
        if (_service.GetConnect() == 1)
        {
            FinalizeSuccessfulConnection(ip, autoRunAfterConnect);
        }
        else
        {
            _pendingConnectionConfirmationIp = ip;
            _pendingAutoRunAfterConnect = autoRunAfterConnect;
            AddRuntimeLog($"已发起设备初始化：{ip}。当前尚未确认连接成功。");
        }
    }

    private void FinalizeSuccessfulConnection(string ip, bool autoRunAfterConnect)
    {
        _pendingConnectionConfirmationIp = null;
        _pendingAutoRunAfterConnect = false;

        if (TryAutoSyncCurrentChannelConfigAfterConnect(out string syncMessage))
        {
            AddRuntimeLog($"设备连接成功：{ip}。{syncMessage}");
        }
        else
        {
            AddRuntimeLog($"设备连接成功：{ip}。当前通道尚未自动同步配置。");
        }

        if (autoRunAfterConnect)
        {
            if (TryStartRunCore(autoTriggered: true, showMessageBox: false, out string errorMessage))
            {
                AddRuntimeLog("已根据启动自动运行设置自动开始运行。");
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                AddRuntimeLog($"自动开始运行失败：{errorMessage}");
            }
        }
    }

    private void ApplyConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveCurrentParameters(showMessageBox: true, out _))
        {
            return;
        }

        if (AcquisitionParameterDialogOverlay is not null)
        {
            AcquisitionParameterDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void BrowseCoefficientFileButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        int channel = GetSelectedMonitorChannelIndex();
        var dialog = new OpenFileDialog
        {
            Title = "选择系统系数文件",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        string preferredDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (Directory.Exists(preferredDirectory))
        {
            dialog.InitialDirectory = preferredDirectory;
        }

        if (!string.IsNullOrWhiteSpace(CoefficientFilePathTextBox.Text) && File.Exists(CoefficientFilePathTextBox.Text))
        {
            dialog.FileName = CoefficientFilePathTextBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            if (!TryValidateCoefficientFileChannel(dialog.FileName, channel, out string validationMessage))
            {
                SetCoefficientStatus(validationMessage, false);
                AppMessageDialog.ShowInfo(this, "系数文件", validationMessage);
                return;
            }

            _coefficientFilePathsByChannel[channel] = dialog.FileName;
            _loadedCoefficientProfilesByChannel.Remove(channel);
            if (_activeCoefficientChannel == channel)
            {
                _loadedCoefficientProfile = null;
            }
            CoefficientFilePathTextBox.Text = dialog.FileName;
            InvalidateAppliedHardwareConfigCache();
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 已选择系数文件，尚未加载。", false);
            SaveUiState();
        }
    }

    private void LoadCoefficientFileButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        int channel = GetSelectedMonitorChannelIndex();
        string path = CoefficientFilePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            AppMessageDialog.ShowInfo(this, "系数文件", "请先选择系统系数文件。");
            return;
        }

        if (!TryValidateCoefficientFileChannel(path, channel, out string validationMessage))
        {
            SetCoefficientStatus(validationMessage, false);
            AppMessageDialog.ShowInfo(this, "系数文件", validationMessage);
            return;
        }

        try
        {
            LoadedCoefficientProfile profile = LoadCoefficientProfile(path);
            _coefficientFilePathsByChannel[channel] = path;
            _loadedCoefficientProfilesByChannel[channel] = profile;
            _loadedCoefficientProfile = profile;
            _activeCoefficientChannel = channel;
            ApplyLoadedCoefficientProfileToUi(profile, channel, addRuntimeLog: true);
            TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out _);
            SaveUiState();
        }
        catch (Exception ex)
        {
            _loadedCoefficientProfilesByChannel.Remove(channel);
            if (_activeCoefficientChannel == channel)
            {
                _loadedCoefficientProfile = null;
            }
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 系数文件加载失败：{ex.Message}", false);
            AppMessageDialog.ShowInfo(this, "系数文件", $"加载系统系数文件失败：\n{ex.Message}");
        }
    }

    private void SaveCoefficientFileButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = TrySaveCoefficientFileForChannel(GetSelectedMonitorChannelIndex());
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "系数文件", error);
        }
    }

    private void GenerateBaselineWavelengthButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedMonitorOption()?.IsAllChannels == true)
        {
            if (_lastSnapshot is null)
            {
                AppMessageDialog.ShowInfo(this, "基准波长", "当前没有可用的实时波长数据，请先连接设备并开始运行。");
                return;
            }

            int snapshotChannel = _lastSnapshot.Channel;
            List<int> enabledChannels = GetEnabledParameterChannelIndexes();
            if (!enabledChannels.Contains(snapshotChannel))
            {
                AppMessageDialog.ShowInfo(this, "基准波长", $"当前最新数据来自{FormatChannelLabel(snapshotChannel)}，但该通道未在采集参数页勾选。");
                return;
            }

            if (!TryGenerateBaselineWavelengthForChannel(snapshotChannel, out string message))
            {
                AppMessageDialog.ShowInfo(this, "基准波长", message);
                return;
            }

            bool shouldSyncHardware = _service is not null && _service.GetState() != 4 && _service.GetState() != 5;
            if (shouldSyncHardware && !TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string applyError))
            {
                AddRuntimeLog($"温度基准波长已写入系数文件，但同步到设备失败：{applyError}");
            }

            SetCoefficientStatus(message, true);
            SaveUiState();
            AddRuntimeLog(message);
            return;
        }

        int configuredChannel = GetSelectedMonitorChannelIndex();
        if (!TryGenerateBaselineWavelengthForChannel(configuredChannel, out string singleMessage))
        {
            AppMessageDialog.ShowInfo(this, "基准波长", singleMessage);
            return;
        }

        bool shouldSyncCurrentHardware = _service is not null && _service.GetState() != 4 && _service.GetState() != 5;
        if (shouldSyncCurrentHardware && !TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string singleApplyError))
        {
            AddRuntimeLog($"温度基准波长已写入系数文件，但同步到设备失败：{singleApplyError}");
        }

        SetCoefficientStatus(singleMessage, true);
        SaveUiState();
        AddRuntimeLog(singleMessage);
    }

    private void GenerateStrainBaselineWavelengthButton_Click(object sender, RoutedEventArgs e)
    {
        int configuredChannel = GetSelectedMonitorOption()?.IsAllChannels == true
            ? (_lastSnapshot?.Channel ?? GetSelectedMonitorChannelIndex())
            : GetSelectedMonitorChannelIndex();

        if (!TryGenerateStrainBaselineWavelengthForChannel(configuredChannel, out string message))
        {
            AppMessageDialog.ShowInfo(this, "应变基准波长", message);
            return;
        }

        ShapeStatusTextBlock.Text = message;
        ResetShapeSensingDisplayForChannel(configuredChannel);
        SaveUiState();
        AddRuntimeLog(message);
        RedrawSelectedChannelViews();
    }

    private void ResetShapeSensingDisplayForChannel(int channel)
    {
        foreach ((int Channel, int SensorIndex) key in _singleSensorStrainTrendByKey.Keys
                     .Where(key => key.Channel == channel)
                     .ToArray())
        {
            _singleSensorStrainTrendByKey.Remove(key);
        }

        _latestAxialStrainByChannel.Remove(channel);
        _singleSensorStrainChartData = null;
        _strainArrayChartData = null;
        _shapeReconstructionChartData = null;
        _latestShapeResult = null;
        _lastShapeSensingTimestampMs = -1;
        _lastShapeSensingRefreshUtc = DateTime.MinValue;
        _singleSensorStrainViewport.Reset();
        _strainArrayViewport.Reset();
        _shapeReconstructionViewport.Reset();
        _shapeReconstructionZoomViewport.Reset();
        SingleSensorStrainCanvas.Children.Clear();
        StrainArrayCanvas.Children.Clear();
        ShapeReconstructionCanvas.Children.Clear();
    }

    private bool TryGenerateStrainBaselineWavelengthForChannel(int configuredChannel, out string message)
    {
        message = string.Empty;
        if (!_snapshotsByChannel.TryGetValue(configuredChannel, out SnapshotModel? snapshot) ||
            snapshot is null ||
            snapshot.SensorWavelengthsNm.Length < 2)
        {
            message = $"{FormatChannelLabel(configuredChannel)} 当前没有足够的实时波长数据，请先连接设备并开始运行。";
            return false;
        }

        LoadedCoefficientProfile? profile = null;
        _ = TryGetCoefficientProfileForChannel(configuredChannel, requireProfile: false, out profile, out _);

        ShapeReconstructionSettings settings = BuildShapeReconstructionSettings(snapshot.SensorWavelengthsNm.Length);
        int minimumWavelengthCount = settings.Mode == ShapeSensingMode.DualFiber ? 4 : 2;
        if (snapshot.SensorWavelengthsNm.Length < minimumWavelengthCount)
        {
            message = settings.Mode == ShapeSensingMode.DualFiber
                ? $"{FormatChannelLabel(configuredChannel)} 当前没有足够的双光纤实时波长数据，无法形成上下配对。"
                : $"{FormatChannelLabel(configuredChannel)} 当前没有足够的单光纤实时波长数据，请先连接设备并开始运行。";
            return false;
        }

        if (!TryGetAveragedShapeBaseline(
            configuredChannel,
            snapshot.SensorWavelengthsNm.Length,
            out float[] averagedBaseline,
            out int baselineFrameCount,
            out float maxStdPm))
        {
            message = $"{FormatChannelLabel(configuredChannel)} 最近有效波长帧不足，至少需要 {ShapeBaselineMinimumFrameCount} 帧稳定数据后再生成应变基准。";
            return false;
        }

        snapshot = CloneSnapshotWithSensorWavelengths(snapshot, averagedBaseline);
        if (profile is not null)
        {
            snapshot = AlignSnapshotSensorDataToProfile(snapshot, profile);
            settings = BuildShapeReconstructionSettings(snapshot.SensorWavelengthsNm.Length);
        }

        int referenceCount;
        if (settings.Mode == ShapeSensingMode.SingleFiber)
        {
            float[] wavelengths = snapshot.SensorWavelengthsNm
                .Where(w => float.IsFinite(w) && w > 0)
                .ToArray();
            if (wavelengths.Length < 2)
            {
                message = $"{FormatChannelLabel(configuredChannel)} 当前没有足够的单光纤实时波长数据，请先连接设备并开始运行。";
                return false;
            }

            _shapeReferenceTopByChannel[configuredChannel] = snapshot.SensorWavelengthsNm.ToArray();
            _shapeReferenceBottomByChannel.Remove(configuredChannel);
            referenceCount = wavelengths.Length;
        }
        else
        {
            if (!TrySplitShapeWavelengthPairs(snapshot, settings, out float[] top, out float[] bottom, out int pairCount, out string splitError))
            {
                message = splitError;
                return false;
            }

            _shapeReferenceTopByChannel[configuredChannel] = top;
            _shapeReferenceBottomByChannel[configuredChannel] = bottom;
            referenceCount = pairCount;
        }

        bool wroteCoefficientFile = false;
        if (profile is not null)
        {
            float[] referenceStrainWavelengths = profile.ReferenceStrainWavelengthsNm.ToArray();
            EnsureFloatArrayLength(ref referenceStrainWavelengths, Math.Max(profile.SensorPositionsRaw.Length, snapshot.SensorWavelengthsNm.Length));
            int updated = 0;
            for (int i = 0; i < snapshot.SensorWavelengthsNm.Length && i < referenceStrainWavelengths.Length; i++)
            {
                float wavelength = snapshot.SensorWavelengthsNm[i];
                if (!float.IsFinite(wavelength) || wavelength <= 0)
                {
                    continue;
                }

                referenceStrainWavelengths[i] = wavelength;
                updated++;
            }

            if (updated > 0)
            {
                profile.ReferenceStrainWavelengthsNm = referenceStrainWavelengths;
                _loadedCoefficientProfilesByChannel[configuredChannel] = profile;
                if (_activeCoefficientChannel == configuredChannel)
                {
                    _loadedCoefficientProfile = profile;
                }

                string? saveError = TrySaveCoefficientFileForChannel(configuredChannel);
                if (!string.IsNullOrWhiteSpace(saveError))
                {
                    message = $"运行时应变基准已生成，但写回系数文件失败：{saveError}";
                    return false;
                }

                InvalidateAppliedHardwareConfigCache();
                wroteCoefficientFile = true;
            }
        }

        string modeText = settings.Mode == ShapeSensingMode.DualFiber ? "双光纤" : "单光纤";
        string unitText = settings.Mode == ShapeSensingMode.DualFiber ? "组上下光栅" : "个光栅";
        string stabilityText = maxStdPm > ShapeBaselineStdWarnPm
            ? $"，基准最大标准差 {maxStdPm:F2} pm，波长仍有波动"
            : $"，基准最大标准差 {maxStdPm:F2} pm";
        message = wroteCoefficientFile
            ? $"{FormatChannelLabel(configuredChannel)} 已用最近 {baselineFrameCount} 帧平均生成{modeText}应变基准波长：{referenceCount} {unitText}，已写回系数文件{stabilityText}。"
            : $"{FormatChannelLabel(configuredChannel)} 已用最近 {baselineFrameCount} 帧平均生成运行时{modeText}应变基准波长：{referenceCount} {unitText}{stabilityText}。";
        return true;
    }

    private bool TrySplitShapeWavelengthPairs(
        SnapshotModel snapshot,
        ShapeReconstructionSettings settings,
        out float[] top,
        out float[] bottom,
        out int pairCount,
        out string error)
    {
        top = Array.Empty<float>();
        bottom = Array.Empty<float>();
        pairCount = 0;
        error = string.Empty;

        float[] wavelengths = snapshot.SensorWavelengthsNm;
        int start = Math.Clamp(settings.StartIndex, 0, wavelengths.Length - 1);
        int end = Math.Clamp(settings.EndIndex == int.MaxValue ? wavelengths.Length - 1 : settings.EndIndex, start, wavelengths.Length - 1);
        int availableCount = end - start + 1;
        int offset = settings.PairOffset > 0 ? settings.PairOffset : availableCount / 2;
        pairCount = Math.Min(offset, availableCount - offset);
        if (pairCount < 2)
        {
            error = $"{FormatChannelLabel(snapshot.Channel)} 当前传感器数量不足，无法形成上下光栅配对。";
            return false;
        }

        top = new float[pairCount];
        bottom = new float[pairCount];
        for (int i = 0; i < pairCount; i++)
        {
            top[i] = wavelengths[start + i];
            bottom[i] = wavelengths[start + offset + i];
        }

        return true;
    }
    private bool TryGenerateBaselineWavelengthForChannel(int configuredChannel, out string message)
    {
        message = string.Empty;
        if (!TryGetCoefficientProfileForChannel(configuredChannel, requireProfile: true, out LoadedCoefficientProfile? profile, out string reason) ||
            profile is null)
        {
            message = reason;
            return false;
        }

        if (!_snapshotsByChannel.TryGetValue(configuredChannel, out SnapshotModel? snapshot) ||
            snapshot is null ||
            snapshot.SensorWavelengthsNm.Length == 0)
        {
            message = $"{FormatChannelLabel(configuredChannel)} 当前没有可用的实时波长数据，请先连接设备并开始运行。";
            return false;
        }

        snapshot = AlignSnapshotSensorDataToProfile(snapshot, profile);
        int count = Math.Min(profile.ReferenceWavelengthsNm.Length, snapshot.SensorWavelengthsNm.Length);
        if (count <= 0)
        {
            message = $"{FormatChannelLabel(configuredChannel)} 系数文件与当前设备数据长度不匹配，无法生成基准波长。";
            return false;
        }

        int updated = 0;
        for (int i = 0; i < count; i++)
        {
            float wavelength = snapshot.SensorWavelengthsNm[i];
            if (!float.IsFinite(wavelength) || wavelength <= 0)
            {
                continue;
            }

            profile.ReferenceWavelengthsNm[i] = wavelength;
            updated++;
        }

        if (updated == 0)
        {
            message = $"{FormatChannelLabel(configuredChannel)} 当前帧没有有效的传感器波长，未生成基准波长。";
            return false;
        }

        _loadedCoefficientProfilesByChannel[configuredChannel] = profile;
        if (_activeCoefficientChannel == configuredChannel)
        {
            _loadedCoefficientProfile = profile;
        }

        InvalidateAppliedHardwareConfigCache();
        ApplyAlarmSettingsToService();
        string? saveError = TrySaveCoefficientFileForChannel(configuredChannel);
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            message = saveError;
            return false;
        }

        bool isRunning = _service is not null && _service.GetState() == 4;
        if (!isRunning)
        {
            SnapshotModel recalculatedSnapshot = RecalculateSnapshotTemperaturesFromCoefficients(snapshot, profile);
            _snapshotsByChannel[configuredChannel] = recalculatedSnapshot;
            if (_lastSnapshot?.Channel == configuredChannel)
            {
                _lastSnapshot = recalculatedSnapshot;
            }

            if (GetSelectedMonitorOption()?.IsAllChannels == true || GetSelectedMonitorChannelIndex() == configuredChannel)
            {
                RefreshSensorOptions(recalculatedSnapshot);
                RedrawSelectedChannelViews();
            }
        }

        bool shouldSyncHardware = _service is not null && _service.GetState() != 4 && _service.GetState() != 5;
        message = shouldSyncHardware
            ? $"{FormatChannelLabel(configuredChannel)} 已依据系数文件中的温度基准值记录当前温度基准波长：更新 {updated} 个传感器点，并已写回系数文件。"
            : $"{FormatChannelLabel(configuredChannel)} 已依据系数文件中的温度基准值记录当前温度基准波长：更新 {updated} 个传感器点，已写回系数文件并刷新当前波形。";
        return true;
    }

    private void ApplyBaselineTemperatureRangeButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        int configuredChannel = GetSelectedMonitorChannelIndex();
        if (_loadedCoefficientProfile is null)
        {
            AppMessageDialog.ShowInfo(this, "基准温度", $"{FormatChannelLabel(configuredChannel)} 请先选择并加载系统系数文件。");
            return;
        }

        if (!TryGetBaselineTemperatureRangeFromUi(out float requestedStartM, out float requestedEndM, out float baselineTemperatureC, out string error))
        {
            AppMessageDialog.ShowInfo(this, "基准温度", error);
            return;
        }

        int count = Math.Min(_loadedCoefficientProfile.DisplaySensorPositionsM.Length, _loadedCoefficientProfile.ReferenceTemperaturesC.Length);
        if (count <= 0)
        {
            AppMessageDialog.ShowInfo(this, "基准温度", "系数文件中没有可更新的温度基准值记录。");
            return;
        }

        float rangeStartM = Math.Min(requestedStartM, requestedEndM);
        float rangeEndM = Math.Max(requestedStartM, requestedEndM);
        int updated = 0;
        float actualStartM = float.NaN;
        float actualEndM = float.NaN;

        for (int i = 0; i < count; i++)
        {
            float positionM = _loadedCoefficientProfile.DisplaySensorPositionsM[i];
            if (!float.IsFinite(positionM) || positionM < rangeStartM || positionM > rangeEndM)
            {
                continue;
            }

            _loadedCoefficientProfile.ReferenceTemperaturesC[i] = baselineTemperatureC;
            actualStartM = updated == 0 ? positionM : Math.Min(actualStartM, positionM);
            actualEndM = updated == 0 ? positionM : Math.Max(actualEndM, positionM);
            updated++;
        }

        if (updated == 0)
        {
            AppMessageDialog.ShowInfo(this, "基准温度", $"在 {rangeStartM:F2} ~ {rangeEndM:F2} m 范围内没有找到可写入的传感器点。");
            return;
        }

        _loadedCoefficientProfilesByChannel[configuredChannel] = _loadedCoefficientProfile;
        InvalidateAppliedHardwareConfigCache();
        ApplyAlarmSettingsToService();
        string? saveError = TrySaveCoefficientFileForChannel(configuredChannel);
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            AppMessageDialog.ShowInfo(this, "基准温度", saveError);
            return;
        }

        if (_snapshotsByChannel.TryGetValue(configuredChannel, out SnapshotModel? currentSnapshot))
        {
            SnapshotModel recalculatedSnapshot = RecalculateSnapshotTemperaturesFromCoefficients(currentSnapshot, _loadedCoefficientProfile);
            _snapshotsByChannel[configuredChannel] = recalculatedSnapshot;
            if (_lastSnapshot?.Channel == configuredChannel)
            {
                _lastSnapshot = recalculatedSnapshot;
            }

            if (GetSelectedMonitorChannelIndex() == configuredChannel)
            {
                RefreshSensorOptions(recalculatedSnapshot);
                RedrawSelectedChannelViews();
            }
        }

        bool shouldSyncHardware = _service is not null && _service.GetState() != 4 && _service.GetState() != 5;
        if (shouldSyncHardware && !TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string applyError))
        {
            AddRuntimeLog($"温度基准值已写入系数文件，但同步到设备失败：{applyError}");
        }

        string successMessage = shouldSyncHardware
            ? $"{FormatChannelLabel(configuredChannel)} 已将 {actualStartM:F2} ~ {actualEndM:F2} m 范围内 {updated} 个传感器点的温度基准值设为 {baselineTemperatureC:F2} ℃，并已写回系数文件。"
            : $"{FormatChannelLabel(configuredChannel)} 已将 {actualStartM:F2} ~ {actualEndM:F2} m 范围内 {updated} 个传感器点的温度基准值设为 {baselineTemperatureC:F2} ℃，已写回系数文件并刷新当前波形。";
        SetCoefficientStatus(successMessage, true);
        SaveUiState();
        AddRuntimeLog(successMessage);
    }

    private static SnapshotModel RecalculateSnapshotTemperaturesFromCoefficients(SnapshotModel snapshot, LoadedCoefficientProfile profile)
    {
        snapshot = AlignSnapshotSensorDataToProfile(snapshot, profile);
        int sensorCount = Math.Min(
            profile.TempSensitivityPmPerC.Length,
            Math.Min(profile.ReferenceTemperaturesC.Length, profile.ReferenceWavelengthsNm.Length));

        if (sensorCount <= 0)
        {
            return snapshot;
        }

        int targetSensorCount = Math.Max(
            snapshot.SensorTemperaturesC.Length,
            Math.Max(snapshot.SensorWavelengthsNm.Length, sensorCount));
        if (targetSensorCount <= 0)
        {
            return snapshot;
        }

        float[] sensorTemps = snapshot.SensorTemperaturesC.Length > 0
            ? snapshot.SensorTemperaturesC.ToArray()
            : Enumerable.Repeat(float.NaN, targetSensorCount).ToArray();

        if (sensorTemps.Length < targetSensorCount)
        {
            Array.Resize(ref sensorTemps, targetSensorCount);
        }

        for (int i = 0; i < sensorCount; i++)
        {
            if (i >= snapshot.SensorWavelengthsNm.Length || i >= sensorTemps.Length)
            {
                continue;
            }

            float wavelength = snapshot.SensorWavelengthsNm[i];
            float sensitivityPm = profile.TempSensitivityPmPerC[i];
            float referenceTemperature = profile.ReferenceTemperaturesC[i];
            float referenceWavelength = profile.ReferenceWavelengthsNm[i];

            if (!float.IsFinite(wavelength) ||
                !float.IsFinite(sensitivityPm) ||
                Math.Abs(sensitivityPm) <= 0.0001f ||
                !float.IsFinite(referenceTemperature) ||
                !float.IsFinite(referenceWavelength) ||
                referenceWavelength <= 0)
            {
                if (i >= snapshot.SensorTemperaturesC.Length || !float.IsFinite(snapshot.SensorTemperaturesC[i]))
                {
                    sensorTemps[i] = float.NaN;
                }
                continue;
            }

            sensorTemps[i] = referenceTemperature + ((wavelength - referenceWavelength) * 1000f / sensitivityPm);
        }

        float[] temperatures = snapshot.TemperaturesC.Length > 0
            ? snapshot.TemperaturesC.ToArray()
            : sensorTemps.ToArray();

        if (temperatures.Length == sensorTemps.Length)
        {
            Array.Copy(sensorTemps, temperatures, sensorTemps.Length);
        }

        float minTemp = float.NaN;
        float maxTemp = float.NaN;
        float avgTemp = float.NaN;
        float maxPos = float.NaN;
        float sumTemp = 0f;
        int validCount = 0;

        float[] positions = snapshot.SensorPositionsM.Length > 0 ? snapshot.SensorPositionsM : snapshot.PositionsM;
        for (int i = 0; i < sensorTemps.Length; i++)
        {
            float temp = sensorTemps[i];
            if (!float.IsFinite(temp))
            {
                continue;
            }

            float pos = i < positions.Length ? positions[i] : float.NaN;
            if (validCount == 0 || temp < minTemp)
            {
                minTemp = temp;
            }

            if (validCount == 0 || temp > maxTemp)
            {
                maxTemp = temp;
                maxPos = pos;
            }

            sumTemp += temp;
            validCount++;
        }

        if (validCount > 0)
        {
            avgTemp = sumTemp / validCount;
        }

        int selectedSensorIndex = snapshot.SpectrumSensorIndex;
        float selectedSensorTemp = selectedSensorIndex >= 0 && selectedSensorIndex < sensorTemps.Length
            ? sensorTemps[selectedSensorIndex]
            : float.NaN;

        return new SnapshotModel
        {
            Timestamp = snapshot.Timestamp,
            TimestampMs = snapshot.TimestampMs,
            Channel = snapshot.Channel,
            PositionsM = snapshot.PositionsM,
            TemperaturesC = temperatures,
            SensorPositionsM = snapshot.SensorPositionsM,
            SensorTemperaturesC = sensorTemps,
            SensorWavelengthsNm = snapshot.SensorWavelengthsNm,
            SpectrumXAxisNm = snapshot.SpectrumXAxisNm,
            SpectrumValues = snapshot.SpectrumValues,
            SpectrumSensorIndex = snapshot.SpectrumSensorIndex,
            SpectrumSensorPositionM = snapshot.SpectrumSensorPositionM,
            SpectrumSensorWavelengthNm = snapshot.SpectrumSensorWavelengthNm,
            SpectrumSensorTemperatureC = selectedSensorTemp,
            Alarms = snapshot.Alarms,
            MinTemp = minTemp,
            MaxTemp = maxTemp,
            AvgTemp = avgTemp,
            MaxPosM = maxPos,
            StatusOk = validCount > 0
        };
    }

    private void StartCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCalibrationWindow();
    }

    private void StopCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = TryStopCalibrationSession(GetSelectedMonitorChannelIndex());
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppMessageDialog.ShowInfo(this, "校准", error);
            return;
        }

        _calibrationWindow?.NotifyCalibrationStopped();
    }

    private void ShowCalibrationWindow()
    {
        if (_service is null)
        {
            return;
        }

        if (_calibrationWindow is null)
        {
            _calibrationWindow = new CalibrationWindow(
                channel => BuildCalibrationWindowParameters(channel),
                (channel, threshold, current) => TryStartCalibrationSessionAsync(channel, threshold, current),
                channel => TryStopCalibrationSessionAsync(channel),
                (channel, current) => TrySetCalibrationCurrentAsync(channel, current),
                (channel, threshold) => TryRecalculateCalibrationPositionsAsync(channel, threshold),
                (channel, rows) => TrySaveCoefficientFileForChannel(channel, rows),
                channel => GetCalibrationResultForChannel(channel),
                channel => GetCalibrationWaveDataForChannel(channel),
                () => _service?.GetAmplifierCurrent() ?? 0,
                channel => GetEditedCalibrationRows(channel),
                channel => GetCalibrationRowsFromCoefficientProfile(channel),
                (channel, rows) => PersistEditedCalibrationRows(channel, rows))
            {
                Owner = this
            };
            _calibrationWindow.Closed += (_, _) => _calibrationWindow = null;
            _calibrationWindow.Show();
        }
        else
        {
            _calibrationWindow.UpdateParameters(BuildCalibrationWindowParameters(GetSelectedMonitorChannelIndex()));
            _calibrationWindow.Activate();
        }
    }

    private CalibrationWindow.CalibrationWindowParameters BuildCalibrationWindowParameters(int channel)
    {
        int calibrationCurrent = ParseInt(EdfaCurrentTextBox.Text, 61);
        float threshold = _calibrationThresholdsByChannel.TryGetValue(channel, out float savedThreshold) && float.IsFinite(savedThreshold) && savedThreshold > 0
            ? savedThreshold
            : 0.5f;

        return new CalibrationWindow.CalibrationWindowParameters
        {
            ChannelIndex = channel,
            AvailableChannelIndexes = GetCalibrationAvailableChannels(channel),
            Threshold = threshold,
            CalibrationCurrentMa = calibrationCurrent,
            FiberLengthM = ParseInt(FiberLengthTextBox.Text, 360),
            StartLengthM = ParseFloat(DelayTextBox.Text, 0),
            CenterWavelengths = ParseFloatArray(CenterWavelengthTextBox.Text, new[] { 1550f }),
            MultiWaveReverse = ParseInt(MultiWaveReverseTextBox.Text, 0) != 0
        };
    }

    private IReadOnlyList<int> GetCalibrationAvailableChannels(int preferredChannel)
    {
        List<int> enabledChannels = _parameterChannelSettings
            .Where(x => x.IsEnabled)
            .Select(x => x.ChannelIndex)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (OpticSwitchEnabledCheckBox?.IsChecked == true)
        {
            if (enabledChannels.Count > 0)
            {
                return enabledChannels;
            }
        }

        if (enabledChannels.Count == 1)
        {
            return enabledChannels;
        }

        return new[] { Math.Clamp(preferredChannel, 0, MaxMonitorChannels - 1) };
    }

    private List<int> GetEnabledParameterChannelIndexes()
    {
        return _parameterChannelSettings
            .Where(x => x.IsEnabled)
            .Select(x => x.ChannelIndex)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private IReadOnlyList<CalibrationWindow.CalibrationRowItem>? GetEditedCalibrationRows(int channel)
    {
        if (!_editedCalibrationRowsByChannel.TryGetValue(channel, out List<CalibrationWindow.CalibrationRowItem>? rows))
        {
            return null;
        }

        return CloneCalibrationRows(rows);
    }

    private IReadOnlyList<CalibrationWindow.CalibrationRowItem>? GetCalibrationRowsFromCoefficientProfile(int channel)
    {
        if (!_loadedCoefficientProfilesByChannel.TryGetValue(channel, out LoadedCoefficientProfile? profile))
        {
            if (_activeCoefficientChannel == channel && _loadedCoefficientProfile is not null)
            {
                profile = _loadedCoefficientProfile;
            }
            else
            {
                return null;
            }
        }

        var rows = new List<CalibrationWindow.CalibrationRowItem>(profile.SensorPositionsRaw.Length);
        for (int i = 0; i < profile.SensorPositionsRaw.Length; i++)
        {
            rows.Add(new CalibrationWindow.CalibrationRowItem
            {
                SourceIndex = i,
                RelativeSamplePoint = profile.SensorPositionsRaw[i],
                Index = i + 1,
                SamplePoint = profile.SensorPositionsRaw[i],
                PositionM = i < profile.DisplaySensorPositionsM.Length
                    ? profile.DisplaySensorPositionsM[i]
                    : profile.SensorPositionsRaw[i] * SensorRawPositionScaleToMeters,
                WaveIndex = i < profile.SensorWaveIndexes.Length ? profile.SensorWaveIndexes[i] : 0
            });
        }

        return CloneCalibrationRows(rows);
    }

    private void PersistEditedCalibrationRows(int channel, IReadOnlyList<CalibrationWindow.CalibrationRowItem> rows)
    {
        _editedCalibrationRowsByChannel[channel] = CloneCalibrationRows(rows);
    }

    private void ClearEditedCalibrationRows(int channel)
    {
        _editedCalibrationRowsByChannel.Remove(channel);
    }

    private static List<CalibrationWindow.CalibrationRowItem> CloneCalibrationRows(IReadOnlyList<CalibrationWindow.CalibrationRowItem> rows)
    {
        var clones = rows
            .OrderBy(x => x.SamplePoint)
            .ThenBy(x => x.WaveIndex)
            .ThenBy(x => x.SourceIndex)
            .Select((row, index) => new CalibrationWindow.CalibrationRowItem
            {
                SourceIndex = row.SourceIndex,
                RelativeSamplePoint = row.RelativeSamplePoint,
                Index = index + 1,
                SamplePoint = row.SamplePoint,
                PositionM = row.PositionM,
                WaveIndex = row.WaveIndex
            })
            .ToList();

        return clones;
    }

    private async Task<string?> TrySetCalibrationCurrentAsync(int channel, int calibrationCurrent)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (calibrationCurrent < 0)
        {
            return "校准电流无效。";
        }

        SelectMonitorChannel(channel);
        ChannelTextBox.Text = (channel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        EdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaPaCurrentTextBox.Text = EdfaPaCurrentTextBox.Text;
        _config = BuildConfigFromUi();
        _config.Channel = channel;
        _config.EdfaCurrentMa = calibrationCurrent;
        _config.CalibrationEdfaCurrentMa = calibrationCurrent;
        _config.CalibrationEdfaPaCurrentMa = _config.EdfaPaCurrentMa;

        int paCurrent = _config.EdfaPaCurrentMa;
        int rc = await Task.Run(() => _service.SetAmplifierCurrents(calibrationCurrent, paCurrent));
        if (rc != 0)
        {
            AddRuntimeLog($"设置校准电流失败：rc={rc}");
            return BuildFriendlyHardwareError("设置校准电流", rc);
        }

        SaveUiState();
        AddRuntimeLog($"{FormatChannelLabel(channel)} 已设置校准PI电流：{calibrationCurrent} mA，PA保持 {paCurrent} mA");
        return null;
    }

    private async Task<string?> TryStartCalibrationSessionAsync(int channel, float threshold, int calibrationCurrent)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (!float.IsFinite(threshold) || threshold <= 0)
        {
            return "校准阈值无效。";
        }

        SelectMonitorChannel(channel);
        ChannelTextBox.Text = (channel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        EdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaPaCurrentTextBox.Text = EdfaPaCurrentTextBox.Text;
        _calibrationThresholdsByChannel[channel] = threshold;
        ClearEditedCalibrationRows(channel);
        int currentState = _service.GetState();
        if (currentState == 4)
        {
            return "设备当前正在运行，请先停止运行，再开始校准。";
        }

        if (currentState == 5)
        {
            return "设备当前仍处于校准中，请先停止校准，再重新开始。";
        }

        HardwareConfig calibrationConfig = BuildCalibrationConfigFromUi(channel, calibrationCurrent);
        int applyRc = await Task.Run(() => _service.ApplyConfig(calibrationConfig));
        if (applyRc != 0)
        {
            AddRuntimeLog($"校准配置同步失败：rc={applyRc}");
            return BuildFriendlyHardwareError("同步校准配置", applyRc);
        }

        _config = calibrationConfig;
        InvalidateAppliedHardwareConfigCache();
        AddRuntimeLog($"{FormatChannelLabel(channel)} 校准前已同步独立校准配置。");
        AddRuntimeLog($"{FormatChannelLabel(channel)} 校准初始位置(raw前10)=[{string.Join(", ", calibrationConfig.SensorPositionsM.Take(10))}]，起始长度={calibrationConfig.DelayNs:F1} m");

        int currentRc = await Task.Run(() => _service.SetAmplifierCurrents(calibrationCurrent, calibrationConfig.EdfaPaCurrentMa));
        if (currentRc != 0)
        {
            AddRuntimeLog($"切换校准电流失败：rc={currentRc}");
            return BuildFriendlyHardwareError("切换校准电流", currentRc);
        }

        int rc = await Task.Run(() => _service.StartCalibration(calibrationConfig.Channel, threshold));
        if (rc != 0)
        {
            _ = Task.Run(() => _service.SetAmplifierCurrents(calibrationConfig.EdfaCurrentMa, calibrationConfig.EdfaPaCurrentMa));
            AddRuntimeLog($"开始校准失败：rc={rc}");
            return BuildFriendlyHardwareError("开始校准", rc);
        }

        AddRuntimeLog($"开始校准。{FormatChannelLabel(channel)} 阈值={threshold:F2}，校准PI={calibrationCurrent} mA，校准PA={calibrationConfig.EdfaPaCurrentMa} mA");
        RefreshConnectionState();
        SaveUiState();
        return null;
    }

    private async Task<string?> TryStopCalibrationSessionAsync(int channel)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        int rc = await Task.Run(() => _service.StopCalibration(channel));
        if (rc != 0)
        {
            AddRuntimeLog($"停止校准失败：rc={rc}");
            return BuildFriendlyHardwareError("停止校准", rc);
        }

        AddRuntimeLog("停止校准。");
        RefreshConnectionState();
        return null;
    }

    private async Task<string?> TryRecalculateCalibrationPositionsAsync(int channel, float threshold)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (!float.IsFinite(threshold) || threshold <= 0)
        {
            return "校准阈值无效。";
        }

        int rc = await Task.Run(() => _service.RecalculateCalibrationPositions(threshold));
        if (rc != 0)
        {
            return BuildFriendlyHardwareError("光栅位置计算", rc);
        }

        AddRuntimeLog($"{FormatChannelLabel(channel)} 已按阈值 {threshold:F2} 重新计算光栅位置。");
        return null;
    }

    private string? TrySetCalibrationCurrent(int channel, int calibrationCurrent)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (calibrationCurrent < 0)
        {
            return "校准电流无效。";
        }

        SelectMonitorChannel(channel);
        ChannelTextBox.Text = (channel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        EdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaPaCurrentTextBox.Text = EdfaPaCurrentTextBox.Text;
        _config = BuildConfigFromUi();
        _config.Channel = channel;
        _config.EdfaCurrentMa = calibrationCurrent;
        _config.CalibrationEdfaCurrentMa = calibrationCurrent;
        _config.CalibrationEdfaPaCurrentMa = _config.EdfaPaCurrentMa;

        int rc = _service.SetAmplifierCurrents(calibrationCurrent, _config.EdfaPaCurrentMa);
        if (rc != 0)
        {
            AddRuntimeLog($"设置校准电流失败：rc={rc}");
            return BuildFriendlyHardwareError("设置校准电流", rc);
        }

        SaveUiState();
        AddRuntimeLog($"{FormatChannelLabel(channel)} 已设置校准PI电流：{calibrationCurrent} mA，PA保持 {_config.EdfaPaCurrentMa} mA");
        return null;
    }

    private string? TryStartCalibrationSession(int channel, float threshold, int calibrationCurrent)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (!float.IsFinite(threshold) || threshold <= 0)
        {
            return "校准阈值无效。";
        }

        SelectMonitorChannel(channel);
        ChannelTextBox.Text = (channel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        EdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaCurrentTextBox.Text = calibrationCurrent.ToString(CultureInfo.InvariantCulture);
        CalibrationEdfaPaCurrentTextBox.Text = EdfaPaCurrentTextBox.Text;
        _calibrationThresholdsByChannel[channel] = threshold;
        ClearEditedCalibrationRows(channel);
        int currentState = _service.GetState();
        if (currentState == 4)
        {
            return "设备当前正在运行，请先停止运行，再开始校准。";
        }

        if (currentState == 5)
        {
            return "设备当前仍处于校准中，请先停止校准，再重新开始。";
        }

        HardwareConfig calibrationConfig = BuildCalibrationConfigFromUi(channel, calibrationCurrent);

        int applyRc = _service.ApplyConfig(calibrationConfig);
        if (applyRc != 0)
        {
            AddRuntimeLog($"校准配置同步失败：rc={applyRc}");
            return BuildFriendlyHardwareError("同步校准配置", applyRc);
        }

        _config = calibrationConfig;
        InvalidateAppliedHardwareConfigCache();
        AddRuntimeLog($"{FormatChannelLabel(channel)} 校准前已同步独立校准配置。");
        AddRuntimeLog($"{FormatChannelLabel(channel)} 校准初始位置(raw前10)=[{string.Join(", ", calibrationConfig.SensorPositionsM.Take(10))}]，起始长度={calibrationConfig.DelayNs:F1} m");

        int currentRc = _service.SetAmplifierCurrents(calibrationCurrent, calibrationConfig.EdfaPaCurrentMa);
        if (currentRc != 0)
        {
            AddRuntimeLog($"切换校准电流失败：rc={currentRc}");
            return BuildFriendlyHardwareError("切换校准电流", currentRc);
        }

        int rc = _service.StartCalibration(_config.Channel, threshold);
        if (rc != 0)
        {
            _service.SetAmplifierCurrents(_config.EdfaCurrentMa, _config.EdfaPaCurrentMa);
            AddRuntimeLog($"开始校准失败：rc={rc}");
            return BuildFriendlyHardwareError("开始校准", rc);
        }

        AddRuntimeLog($"开始校准。{FormatChannelLabel(channel)} 阈值={threshold:F2}，校准PI={calibrationCurrent} mA，校准PA={calibrationConfig.EdfaPaCurrentMa} mA");
        RefreshConnectionState();
        SaveUiState();
        return null;
    }

    private string? TryStopCalibrationSession(int channel)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        SelectMonitorChannel(channel);
        ChannelTextBox.Text = (channel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        _config = BuildConfigFromUi();
        _config.Channel = channel;

        int rc = _service.StopCalibration(channel);
        if (rc != 0)
        {
            AddRuntimeLog($"停止校准失败：rc={rc}");
            return BuildFriendlyHardwareError("停止校准", rc);
        }

        CalibrationResultModel? calibration = _service.TryReadLatestCalibrationResult();
        if (calibration is null || calibration.SensorPositionsRaw.Length == 0)
        {
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 停止校准成功，但未收到有效的光栅位置数据。", false);
            AddRuntimeLog("停止校准。未收到有效校准结果。");
            return null;
        }

        ApplyCalibrationDraft(calibration, _config);
        InvalidateAppliedHardwareConfigCache();
        SaveUiState();
        SetCoefficientStatus($"{FormatChannelLabel(calibration.Channel)} 校准完成，已生成系统系数草稿，请保存系数；后续运行前会自动同步。", true);
        AddRuntimeLog($"{FormatChannelLabel(calibration.Channel)} 停止校准。已生成 {calibration.SensorPositionsRaw.Length} 个传感器点的系统系数草稿。");
        return null;
    }

    private void SelectMonitorChannel(int channel)
    {
        channel = Math.Clamp(channel, 0, MaxMonitorChannels - 1);
        ChannelOption? option = _channelOptions.FirstOrDefault(x => x.ChannelIndex == channel);
        if (option is null)
        {
            return;
        }

        SetSelectedChannelControls(option);
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
    }

    private CalibrationResultModel? GetCalibrationResultForChannel(int channel)
    {
        CalibrationResultModel? result = _service?.TryReadLatestCalibrationResult();
        return result is not null && result.Channel == channel ? result : null;
    }

    private CalibrationWaveDataModel? GetCalibrationWaveDataForChannel(int channel)
    {
        CalibrationWaveDataModel? waveData = _service?.TryReadLatestCalibrationWaveData();
        return waveData is not null && waveData.Channel == channel ? waveData : null;
    }

    private string? TryRecalculateCalibrationPositions(int channel, float threshold)
    {
        if (_service is null)
        {
            return "监控服务尚未初始化。";
        }

        if (!float.IsFinite(threshold) || threshold <= 0)
        {
            return "校准阈值无效。";
        }

        SelectMonitorChannel(channel);
        _calibrationThresholdsByChannel[channel] = threshold;
        int rc = _service.RecalculateCalibrationPositions(threshold);
        if (rc != 0)
        {
            return BuildFriendlyHardwareError("重新计算光栅位置", rc);
        }

        AddRuntimeLog($"{FormatChannelLabel(channel)} 已按阈值 {threshold:F2} 重新计算光栅位置。");
        return null;
    }

    private string? TrySaveCoefficientFileForChannel(int channel, IReadOnlyList<CalibrationWindow.CalibrationRowItem>? editedRows = null)
    {
        SelectMonitorChannel(channel);

        if (editedRows is not null)
        {
            if (_loadedCoefficientProfile is null)
            {
                CalibrationResultModel? calibration = GetCalibrationResultForChannel(channel);
                if (calibration is null || calibration.SensorPositionsRaw.Length == 0)
                {
                    return $"{FormatChannelLabel(channel)} 当前没有可编辑的系统系数数据。请先完成一次校准。";
                }

                int calibrationCurrent = ParseInt(CalibrationEdfaCurrentTextBox.Text, ParseInt(EdfaCurrentTextBox.Text, 61));
                HardwareConfig calibrationConfig = BuildCalibrationConfigFromUi(channel, calibrationCurrent);
                _loadedCoefficientProfile = BuildCoefficientProfileFromCalibration(calibration, calibrationConfig);
                _loadedCoefficientProfilesByChannel[channel] = _loadedCoefficientProfile;
            }

            PersistEditedCalibrationRows(channel, editedRows);
            _loadedCoefficientProfile = BuildCoefficientProfileFromCalibrationRows(_loadedCoefficientProfile, editedRows);
            _loadedCoefficientProfilesByChannel[channel] = _loadedCoefficientProfile;
        }

        if (_loadedCoefficientProfile is null)
        {
            return $"{FormatChannelLabel(channel)} 当前没有可保存的系统系数数据。请先完成一次校准或加载现有系数文件。";
        }

        string savePath = ResolveCoefficientSavePath(channel);
        if (!TryValidateCoefficientFileChannel(savePath, channel, out string validationMessage))
        {
            SetCoefficientStatus(validationMessage, false);
            return validationMessage;
        }

        try
        {
            HardwareConfig saveConfig = BuildBaseConfigForChannel(channel);
            RepairProfileWaveIndexesFromReferenceWavelengths(_loadedCoefficientProfile, saveConfig.CenterWavelengths);
            SaveCoefficientProfile(savePath, _loadedCoefficientProfile);
            _loadedCoefficientProfile.FilePath = savePath;
            _loadedCoefficientProfilesByChannel[channel] = _loadedCoefficientProfile;
            _coefficientFilePathsByChannel[channel] = savePath;
            _activeCoefficientChannel = channel;
            CoefficientFilePathTextBox.Text = savePath;
            ApplyAlarmSettingsToService();
            if (_snapshotsByChannel.TryGetValue(channel, out SnapshotModel? currentSnapshot))
            {
                RefreshSensorOptions(currentSnapshot);
                if (GetSelectedMonitorChannelIndex() == channel)
                {
                    RedrawSelectedChannelViews();
                }
            }
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 系统系数文件已保存：{IoPath.GetFileName(savePath)}", true);
            SaveUiState();
            AddRuntimeLog($"{FormatChannelLabel(channel)} 系统系数文件已保存：{IoPath.GetFileName(savePath)}");
            return null;
        }
        catch (Exception ex)
        {
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 系数文件保存失败：{ex.Message}", false);
            return $"保存系统系数文件失败：{ex.Message}";
        }
    }

    private void ApplyCalibrationDraft(CalibrationResultModel calibration, HardwareConfig cfg)
    {
        ClearEditedCalibrationRows(calibration.Channel);
        LoadedCoefficientProfile profile = BuildCoefficientProfileFromCalibration(calibration, cfg);
        _loadedCoefficientProfilesByChannel[calibration.Channel] = profile;
        _loadedCoefficientProfile = profile;
        _activeCoefficientChannel = calibration.Channel;
        if (!string.IsNullOrWhiteSpace(profile.FilePath))
        {
            _coefficientFilePathsByChannel[calibration.Channel] = profile.FilePath;
        }
        else
        {
            _coefficientFilePathsByChannel.Remove(calibration.Channel);
        }

        ApplyLoadedCoefficientProfileToUi(profile, calibration.Channel, addRuntimeLog: false);
    }

    private void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryStartRunCore(autoTriggered: false, showMessageBox: true, out string errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                if (!errorMessage.StartsWith("开始运行失败", StringComparison.Ordinal))
                {
                    AddRuntimeLog($"开始运行被阻止：{errorMessage}");
                }
            }
            return;
        }

        AddRuntimeLog("开始运行。");
    }

    private bool TryStartRunCore(bool autoTriggered, bool showMessageBox, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (_service is null)
        {
            errorMessage = "监控服务尚未初始化。";
            return false;
        }

        if (!TryEnsureCurrentConfigForHardwareAction(out HardwareConfig cfg, out bool allChannelsLowSpeed, out string reason))
        {
            errorMessage = reason;
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "运行", reason);
            }
            return false;
        }

        _config = cfg;
        ApplyAlarmSettingsToService();

        int rc = _service.StartAcquisition(_config.Channel, allChannelsLowSpeed);
        if (rc != 0)
        {
            errorMessage = BuildFriendlyHardwareError("开始运行", rc);
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "运行", errorMessage);
            }

            AddRuntimeLog(autoTriggered
                ? $"自动开始运行失败：rc={rc}"
                : $"开始运行失败：rc={rc}");

            return false;
        }

        return true;
    }

    private bool TrySaveCurrentParameters(bool showMessageBox, out string message)
    {
        message = string.Empty;

        if (_service is null)
        {
            message = "监控服务尚未初始化。";
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "参数", message);
            }
            return false;
        }

        if (!ValidateChannelSelectionBeforeSave(showMessageBox, out message))
        {
            return false;
        }

        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        _config = BuildConfigFromUi();
        SaveUiState();
        InvalidateAppliedHardwareConfigCache();

        if (_service.GetConnect() != 1)
        {
            message = "参数已保存。设备未连接，连接后会自动同步。";
            AddRuntimeLog(message);
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "参数", message);
            }
            return true;
        }

        if (_service.GetState() == 5)
        {
            message = "参数已保存。设备当前处于校准中，校准结束后再自动同步。";
            AddRuntimeLog(message);
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "参数", message);
            }
            return true;
        }

        List<int> enabledChannels = GetEnabledParameterChannelIndexes();
        foreach (int channel in enabledChannels)
        {
            if (TryGetCoefficientProfileForChannel(channel, requireProfile: true, out _, out string profileError))
            {
                continue;
            }

            message = $"参数已保存。{FormatChannelLabel(channel)} 尚未生成系数文件，运行前请先校准。";
            AddRuntimeLog(message);
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "参数", message);
            }
            return true;
        }

        if (!TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string applyError))
        {
            message = $"参数已保存，但{applyError}";
            AddRuntimeLog(message);
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "参数", message);
            }
            return false;
        }

        message = "参数已保存，并同步到设备。";
        AddRuntimeLog(message);
        if (showMessageBox)
        {
            AppMessageDialog.ShowInfo(this, "参数", message);
        }
        return true;
    }

    private void StopRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        int rc = _service.StopAcquisition(_config.Channel);
        if (rc != 0)
        {
            AppMessageDialog.ShowInfo(this, "运行", BuildFriendlyHardwareError("停止运行", rc));
            AddRuntimeLog($"停止运行失败：rc={rc}");
            return;
        }

        AddRuntimeLog("停止运行。");
    }

    private async void QueryHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        if (HistoryStartDateCalendar?.SelectedDate is not DateTime startDate)
        {
            AppMessageDialog.ShowInfo(this, "历史查询", "开始日期无效，请选择正确的日期。");
            return;
        }

        DateTime start = startDate.Date;
        DateTime end = startDate.Date.AddDays(1).AddTicks(-1);

        IReadOnlyList<AlarmRecord> rows = await _service.Store.QueryAlarmEventsAsync(start, end, 2000);
        _historyQueryRows.Clear();
        foreach (AlarmRecord row in rows)
        {
            _historyQueryRows.Add(row);
        }

        RefreshHistoryTypeFilterOptions();
        ApplyHistoryFiltersAndRefreshUi();

        AddRuntimeLog($"历史查询完成：{rows.Count} 条记录。");
    }

    private void HistoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyHistoryFiltersAndRefreshUi();
    }

    private void ResetHistoryFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        SetHistoryStartDate(DateTime.Now.Date);

        if (HistoryChannelFilterComboBox.Items.Count > 0)
        {
            HistoryChannelFilterComboBox.SelectedIndex = 0;
        }

        if (HistoryTypeFilterComboBox.Items.Count > 0)
        {
            HistoryTypeFilterComboBox.SelectedIndex = 0;
        }

        ApplyHistoryFiltersAndRefreshUi();
    }

    private void ExportHistoryExcelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyAlarmItems.Count == 0)
        {
            AppMessageDialog.ShowInfo(this, "历史导出", "当前没有可导出的历史报警记录。");
            return;
        }

        DateTime now = DateTime.Now;
        string deviceNamePart = GetCurrentDeviceExportNamePart();
        var dialog = new SaveFileDialog
        {
            Title = "导出历史报警",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = $"alarm_history_{deviceNamePart}_{now:yyyyMMdd_HHmmss}.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WriteHistoryAlarmExcel(dialog.FileName, BuildHistoryExportRows());
        AddRuntimeLog($"历史报警已导出：{IoPath.GetFileName(dialog.FileName)}");
    }

    private void ExportHistoryCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyAlarmItems.Count == 0)
        {
            AppMessageDialog.ShowInfo(this, "历史导出", "当前没有可导出的历史报警记录。");
            return;
        }

        DateTime now = DateTime.Now;
        string deviceNamePart = GetCurrentDeviceExportNamePart();
        var dialog = new SaveFileDialog
        {
            Title = "导出历史报警",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"alarm_history_{deviceNamePart}_{now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WriteHistoryAlarmCsv(dialog.FileName, BuildHistoryExportRows());
        AddRuntimeLog($"历史报警已导出：{IoPath.GetFileName(dialog.FileName)}");
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        if (!AppMessageDialog.ShowConfirm(
                this,
                "清空历史",
                "将清空本地全部历史报警记录，此操作不可撤销。是否继续？",
                "清空",
                "取消"))
        {
            return;
        }

        await _service.Store.ClearAlarmEventsAsync();
        _historyQueryRows.Clear();
        _historyAlarmItems.Clear();
        RefreshHistoryTypeFilterOptions();
        ApplyHistoryFiltersAndRefreshUi();
        AddRuntimeLog("历史报警记录已清空。");
    }

    private void InitializeHistoryFilterSelectors()
    {
        HistoryChannelFilterComboBox.Items.Clear();
        HistoryChannelFilterComboBox.Items.Add("全部通道");
        for (int i = 0; i < MaxMonitorChannels; i++)
        {
            HistoryChannelFilterComboBox.Items.Add($"通道{i + 1}");
        }

        RefreshHistoryTypeFilterOptions();

        HistoryChannelFilterComboBox.SelectedIndex = 0;
        HistoryTypeFilterComboBox.SelectedIndex = 0;
        SetHistoryStartDate(DateTime.Now.Date);
    }

    private void ToggleHistoryStartDatePopup_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryStartDatePopup is null)
        {
            return;
        }

        HistoryStartDatePopup.IsOpen = !HistoryStartDatePopup.IsOpen;
    }

    private void HistoryStartDateCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HistoryStartDateCalendar?.SelectedDate is not DateTime selectedDate)
        {
            return;
        }

        SetHistoryStartDate(selectedDate.Date);
        if (HistoryStartDatePopup is not null)
        {
            HistoryStartDatePopup.IsOpen = false;
        }
    }

    private void HistoryStartDatePopup_Closed(object? sender, EventArgs e)
    {
        if (HistoryStartDateToggleButton is null)
        {
            return;
        }

        HistoryStartDateToggleButton.Background = BrushFromHex("#214E82");
        HistoryStartDateToggleButton.BorderBrush = BrushFromHex("#5B92D8");
    }

    private void SetHistoryStartDate(DateTime date)
    {
        if (HistoryStartDateCalendar is not null)
        {
            HistoryStartDateCalendar.SelectedDate = date.Date;
            HistoryStartDateCalendar.DisplayDate = date.Date;
        }

        if (HistoryStartDateTextBlock is not null)
        {
            HistoryStartDateTextBlock.Text = date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        }
    }

    private void RefreshHistoryTypeFilterOptions()
    {
        string? selected = HistoryTypeFilterComboBox.SelectedItem as string;
        var types = new List<string>
        {
            "定温报警",
            "差温报警",
            "传感器故障"
        };

        HistoryTypeFilterComboBox.Items.Clear();
        HistoryTypeFilterComboBox.Items.Add("全部类型");
        foreach (string type in types)
        {
            HistoryTypeFilterComboBox.Items.Add(type);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            foreach (object item in HistoryTypeFilterComboBox.Items)
            {
                if (string.Equals(item as string, selected, StringComparison.Ordinal))
                {
                    HistoryTypeFilterComboBox.SelectedItem = item;
                    return;
                }
            }
        }

        HistoryTypeFilterComboBox.SelectedIndex = 0;
    }

    private void ApplyHistoryFiltersAndRefreshUi()
    {
        string selectedChannel = HistoryChannelFilterComboBox.SelectedItem as string ?? "全部通道";
        string selectedType = HistoryTypeFilterComboBox.SelectedItem as string ?? "全部类型";

        _historyAlarmItems.Clear();

        foreach (AlarmRecord row in _historyQueryRows)
        {
            if (selectedChannel != "全部通道" &&
                !string.Equals(row.ChannelText, selectedChannel, StringComparison.Ordinal))
            {
                continue;
            }

            string type = string.IsNullOrWhiteSpace(row.AlarmTypeText) ? "未标注" : row.AlarmTypeText;
            if (selectedType != "全部类型" &&
                !string.Equals(type, selectedType, StringComparison.Ordinal))
            {
                continue;
            }

            _historyAlarmItems.Add(row);
        }
    }

    private static string[] HistoryExportHeaders =>
        new[] { "时间", "通道", "分区", "位置(m)", "传感器序号", "报警类型", "温度(°C)", "触发依据" };

    private List<string[]> BuildHistoryExportRows()
    {
        var rows = new List<string[]>(_historyAlarmItems.Count);
        foreach (AlarmRecord alarm in _historyAlarmItems)
        {
            rows.Add(new[]
            {
                alarm.FullTimeText,
                alarm.ChannelText,
                alarm.ZoneText,
                alarm.PositionM.ToString("F1", CultureInfo.InvariantCulture),
                alarm.SensorIndexDisplay.ToString(CultureInfo.InvariantCulture),
                alarm.AlarmTypeText,
                alarm.TemperatureC.ToString("F2", CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(alarm.DetailText) ? alarm.TypeText : alarm.DetailText
            });
        }

        return rows;
    }

    private static void WriteHistoryAlarmCsv(string filePath, IReadOnlyList<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", HistoryExportHeaders.Select(EscapeCsv)));
        foreach (string[] row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }

        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteHistoryAlarmExcel(string filePath, IReadOnlyList<string[]> rows)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        string[] headers = HistoryExportHeaders;
        double[] columnWidths = CalculateExcelColumnWidths(headers, rows);

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteZipEntry(archive, "[Content_Types].xml", BuildExcelContentTypesXml());
        WriteZipEntry(archive, "_rels/.rels", BuildExcelRootRelsXml());
        WriteZipEntry(archive, "xl/workbook.xml", BuildExcelWorkbookXml());
        WriteZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildExcelWorkbookRelsXml());
        WriteZipEntry(archive, "xl/styles.xml", BuildExcelStylesXml());
        WriteZipEntry(archive, "xl/worksheets/sheet1.xml", BuildExcelWorksheetXml(headers, rows, columnWidths));
    }

    private static double[] CalculateExcelColumnWidths(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = new double[headers.Count];
        for (int i = 0; i < headers.Count; i++)
        {
            widths[i] = Math.Clamp(GetExcelTextWidth(headers[i]) + 2d, 10d, i == headers.Count - 1 ? 72d : 24d);
        }

        foreach (string[] row in rows)
        {
            for (int i = 0; i < headers.Count && i < row.Length; i++)
            {
                double width = GetExcelTextWidth(row[i]) + 2d;
                double max = i == headers.Count - 1 ? 72d : 24d;
                widths[i] = Math.Clamp(Math.Max(widths[i], width), 10d, max);
            }
        }

        return widths;
    }

    private static double GetExcelTextWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 8d;
        }

        double width = 0d;
        foreach (char c in text)
        {
            width += c <= 127 ? 1d : 1.8d;
        }

        return width;
    }

    private static string BuildExcelWorksheetXml(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, IReadOnlyList<double> columnWidths)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
        sb.Append("<cols>");
        for (int i = 0; i < columnWidths.Count; i++)
        {
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{columnWidths[i].ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        }
        sb.Append("</cols><sheetData>");

        AppendExcelRow(sb, 1, headers, 1, 24d);
        for (int i = 0; i < rows.Count; i++)
        {
            int styleIndex = 2;
            AppendExcelRow(sb, i + 2, rows[i], styleIndex, 20d);
        }

        sb.Append("</sheetData>");
        sb.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static void AppendExcelRow(StringBuilder sb, int rowIndex, IReadOnlyList<string> values, int styleIndex, double rowHeight)
    {
        sb.Append($"<row r=\"{rowIndex}\" ht=\"{rowHeight.ToString("0.##", CultureInfo.InvariantCulture)}\" customHeight=\"1\">");
        for (int i = 0; i < values.Count; i++)
        {
            string cellRef = $"{GetExcelColumnName(i + 1)}{rowIndex}";
            sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\" s=\"{styleIndex}\"><is><t xml:space=\"preserve\">{EscapeXml(values[i])}</t></is></c>");
        }
        sb.Append("</row>");
    }

    private static string GetExcelColumnName(int index)
    {
        var sb = new StringBuilder();
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + (index % 26)));
            index /= 26;
        }
        return sb.ToString();
    }

    private static string BuildExcelContentTypesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private static string BuildExcelRootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string BuildExcelWorkbookXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"报警历史\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private static string BuildExcelWorkbookRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private static string BuildExcelStylesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><name val=\"Microsoft YaHei\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Microsoft YaHei\"/></font>" +
        "</fonts>" +
        "<fills count=\"3\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF16345F\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color rgb=\"FF2E5A8F\"/></left><right style=\"thin\"><color rgb=\"FF2E5A8F\"/></right><top style=\"thin\"><color rgb=\"FF2E5A8F\"/></top><bottom style=\"thin\"><color rgb=\"FF2E5A8F\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"3\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
        "</cellXfs>" +
        "</styleSheet>";

    private static void WriteZipEntry(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string EscapeXml(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string EscapeCsv(string? text)
    {
        string value = text ?? string.Empty;
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private void InitializeHistoryDateTimeSelectors(DateTime now)
    {
        SetHistoryStartDate(now.Date);
    }

    private void MainViewTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, MainMonitorTabButton))
        {
            MainTabControl.SelectedIndex = 0;
        }
        else if (ReferenceEquals(sender, MainHistoryTabButton))
        {
            MainTabControl.SelectedIndex = 1;
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, MainTabControl) || !ReferenceEquals(e.OriginalSource, MainTabControl))
        {
            return;
        }

        UpdateMainViewTabButtonStates();
    }

    private void UpdateMainViewTabButtonStates()
    {
        if (MainMonitorTabButton is null || MainHistoryTabButton is null || MainTabControl is null)
        {
            return;
        }

        if (MainTabControl.SelectedIndex < 0 && MainTabControl.Items.Count > 0)
        {
            MainTabControl.SelectedIndex = 0;
        }

        bool monitorSelected = MainTabControl.SelectedIndex <= 0;
        MainMonitorTabButton.Tag = monitorSelected ? "Selected" : null;
        MainHistoryTabButton.Tag = monitorSelected ? null : "Selected";
    }

    private HardwareConfig BuildBaseConfigFromUi()
    {
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        int sdkChannel = Math.Clamp(GetSelectedMonitorChannelIndex(ParseUiChannelToSdkIndex(ChannelTextBox.Text, 0)), 0, MaxMonitorChannels - 1);
        HardwareConfig cfg = BuildBaseConfigForChannel(sdkChannel);
        ChannelTextBox.Text = (sdkChannel + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        return cfg;
    }

    private HardwareConfig BuildBaseConfigForChannel(int channel)
    {
        channel = Math.Clamp(channel, 0, MaxMonitorChannels - 1);
        ParameterChannelSettingItem channelSetting = GetOrCreateParameterChannelSetting(channel);
        string centerWavelengthText = channelSetting.CenterWavelengthText;
        float[] centerWavelengths = ParseFloatArray(centerWavelengthText, new[] { 1532f, 1542f, 1552f });
        int fiberLength = Math.Max(10, ParseInt(FiberLengthTextBox.Text, 1200));
        int profileStep = Math.Clamp(ParseInt(ProfileStepTextBox.Text, DefaultProfileStepMeters), 1, 500);
        int targetProfilePoints = CalcProfilePointsByStep(fiberLength, profileStep);

        if (int.TryParse(FiberLengthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength) &&
            int.TryParse(ProfileStepTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStep) &&
            parsedLength > 0 &&
            parsedStep > 0)
        {
            TargetPointsTextBox.Text = CalcProfilePointsByStep(parsedLength, parsedStep).ToString(CultureInfo.InvariantCulture);
        }
        else if (!_isRestoringUiState)
        {
            TargetPointsTextBox.Text = string.Empty;
        }

        int displayChannel = channel + DisplayChannelBase;
        bool opticSwitchEnabled = ParseInt(OpticSwitchEnabledTextBox.Text, displayChannel > 1 ? 1 : 0) != 0;
        int multiWaveReverseDefault = centerWavelengths.Length > 1 ? 1 : 0;

        return new HardwareConfig
        {
            StartWavelengthNm = ParseInt(StartWlTextBox.Text, 1528),
            StopWavelengthNm = ParseInt(StopWlTextBox.Text, 1552),
            FiberLengthM = fiberLength,
            DelayNs = ParseFloat(DelayTextBox.Text, 10),
            PulseWidth = ParseInt(PulseWidthTextBox.Text, 20),
            TargetProfilePoints = targetProfilePoints,
            OpticSwitchEnabled = opticSwitchEnabled,
            EdfaCurrentMa = ParseInt(EdfaCurrentTextBox.Text, 61),
            EdfaPaCurrentMa = ParseInt(EdfaPaCurrentTextBox.Text, 50),
            CalibrationEdfaCurrentMa = ParseInt(EdfaCurrentTextBox.Text, 61),
            CalibrationEdfaPaCurrentMa = ParseInt(EdfaPaCurrentTextBox.Text, 50),
            FiberDensityMode = ParseInt(FiberDensityTextBox.Text, 0),
            WavelengthAverageCount = Math.Max(1, ParseInt(WavelengthAverageCountTextBox.Text, 1)),
            MultiWaveReverse = ParseInt(MultiWaveReverseTextBox.Text, multiWaveReverseDefault) != 0,
            AutoRun = false,
            SpeedMode = ParseInt(SpeedModeTextBox.Text, 0),
            LaserType = ParseInt(LaserTypeTextBox.Text, 0),
            AlgorithmType = ParseInt(AlgorithmTypeTextBox.Text, 0),
            WavelengthPrecisionMode = ParseInt(WavelengthPrecisionModeTextBox.Text, 0),
            Channel = channel,
            ChannelEnabled = channelSetting.IsEnabled,
            CenterWavelengths = centerWavelengths
        };
    }

    private HardwareConfig BuildConfigFromUi()
    {
        int channel = Math.Clamp(GetSelectedMonitorChannelIndex(ParseUiChannelToSdkIndex(ChannelTextBox.Text, 0)), 0, MaxMonitorChannels - 1);
        return BuildConfigForChannel(channel, _loadedCoefficientProfile, enableOpticSwitchForRun: false);
    }

    private HardwareConfig BuildConfigForChannel(int channel, LoadedCoefficientProfile? profile, bool enableOpticSwitchForRun)
    {
        HardwareConfig cfg = BuildBaseConfigForChannel(channel);
        cfg.OpticSwitchEnabled = enableOpticSwitchForRun || cfg.OpticSwitchEnabled;
        int sensorCount = profile?.SensorPositionsRaw.Length ?? DefaultSensorCount;

        if (profile is not null)
        {
            cfg.CoefficientFilePath = profile.FilePath;
            cfg.SensorPositionsM = profile.SensorPositionsRaw.ToArray();
            cfg.SensorWaveIndexes = ResolveProfileWaveIndexesForConfig(profile, cfg.CenterWavelengths);
            cfg.SensorTempSensitivityPmPerC = profile.TempSensitivityPmPerC.ToArray();
            cfg.SensorStrainSensitivity = profile.StrainSensitivity.ToArray();
            cfg.SensorReferenceTemperaturesC = profile.ReferenceTemperaturesC.ToArray();
            cfg.SensorReferenceStrains = profile.ReferenceStrains.ToArray();
            cfg.SensorReferenceWavelengthsNm = profile.ReferenceWavelengthsNm.ToArray();
            cfg.SensorReferenceStrainWavelengthsNm = profile.ReferenceStrainWavelengthsNm.ToArray();
            cfg.SensorPositionScaleToMeters = profile.PositionScaleToMeters;
        }
        else
        {
            cfg.SensorPositionsM = BuildUniformSensorPositionsRaw(cfg.FiberLengthM, cfg.DelayNs, sensorCount);
            cfg.SensorWaveIndexes = BuildWaveIndexes(cfg.SensorPositionsM.Length, cfg.CenterWavelengths.Length);
            cfg.SensorPositionScaleToMeters = SensorRawPositionScaleToMeters;
        }

        return cfg;
    }

    private static int[] ResolveProfileWaveIndexesForConfig(LoadedCoefficientProfile profile, float[] centerWavelengths)
    {
        int sensorCount = profile.SensorPositionsRaw.Length;
        int[] result = new int[sensorCount];
        int waveCount = Math.Max(1, centerWavelengths.Length);
        for (int i = 0; i < sensorCount; i++)
        {
            float referenceWavelength = ResolveProfileReferenceWavelength(profile, i);
            if (float.IsFinite(referenceWavelength) && referenceWavelength > 0 && centerWavelengths.Length > 0)
            {
                result[i] = FindNearestCenterWavelengthIndex(centerWavelengths, referenceWavelength);
                continue;
            }

            int fallback = i < profile.SensorWaveIndexes.Length ? profile.SensorWaveIndexes[i] : 1;
            result[i] = NormalizeDisplayWaveIndexForConfig(fallback, waveCount);
        }

        return result;
    }

    private static void RepairProfileWaveIndexesFromReferenceWavelengths(LoadedCoefficientProfile profile, float[] centerWavelengths)
    {
        if (profile.SensorPositionsRaw.Length == 0 || centerWavelengths.Length == 0)
        {
            return;
        }

        profile.SensorWaveIndexes = ResolveProfileWaveIndexesForConfig(profile, centerWavelengths)
            .Select(ToDisplayWaveIndex)
            .ToArray();
    }

    private static int NormalizeDisplayWaveIndexForConfig(int waveIndex, int waveCount)
    {
        if (waveCount <= 0)
        {
            return 0;
        }

        if (waveIndex >= 1 && waveIndex <= waveCount)
        {
            return waveIndex - 1;
        }

        return Math.Clamp(waveIndex, 0, waveCount - 1);
    }

    private static int ToDisplayWaveIndex(int zeroBasedWaveIndex)
    {
        return zeroBasedWaveIndex + 1;
    }

    private static int FindNearestCenterWavelengthIndex(float[] centerWavelengths, float wavelength)
    {
        int bestIndex = 0;
        float bestDiff = float.PositiveInfinity;
        for (int i = 0; i < centerWavelengths.Length; i++)
        {
            float center = centerWavelengths[i];
            if (!float.IsFinite(center) || center <= 0)
            {
                continue;
            }

            float diff = Math.Abs(center - wavelength);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private bool TryGetCoefficientProfileForChannel(int channel, bool requireProfile, out LoadedCoefficientProfile? profile, out string reason)
    {
        profile = null;
        reason = string.Empty;

        if (_loadedCoefficientProfilesByChannel.TryGetValue(channel, out LoadedCoefficientProfile? cachedProfile))
        {
            profile = cachedProfile;
            return true;
        }

        if (_activeCoefficientChannel == channel && _loadedCoefficientProfile is not null)
        {
            profile = _loadedCoefficientProfile;
            return true;
        }

        string path = _coefficientFilePathsByChannel.TryGetValue(channel, out string? storedPath)
            ? storedPath
            : string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !IsCoefficientFileInAllowedDirectory(path))
        {
            path = ResolveAutoCoefficientFilePath(channel);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _coefficientFilePathsByChannel[channel] = path;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!requireProfile)
            {
                return true;
            }

            reason = $"{FormatChannelLabel(channel)} 未找到系统系数文件。";
            return false;
        }

        if (!File.Exists(path))
        {
            if (!requireProfile)
            {
                return true;
            }

            reason = $"{FormatChannelLabel(channel)} 未找到系统系数文件。";
            return false;
        }

        if (!TryValidateCoefficientFileChannel(path, channel, out string validationMessage))
        {
            if (!requireProfile)
            {
                return true;
            }

            reason = validationMessage;
            return false;
        }

        try
        {
            profile = LoadCoefficientProfile(path);
            _loadedCoefficientProfilesByChannel[channel] = profile;
            _coefficientFilePathsByChannel[channel] = profile.FilePath;
            return true;
        }
        catch (Exception ex)
        {
            if (!requireProfile)
            {
                return true;
            }

            reason = $"{FormatChannelLabel(channel)} 系数文件加载失败：{ex.Message}";
            return false;
        }
    }

    private bool TryBuildConfigForChannel(int channel, bool enableOpticSwitchForRun, bool requireCoefficientProfile, out HardwareConfig config, out string reason)
    {
        config = new HardwareConfig();
        if (!TryGetCoefficientProfileForChannel(channel, requireCoefficientProfile, out LoadedCoefficientProfile? profile, out reason))
        {
            return false;
        }

        config = BuildConfigForChannel(channel, profile, enableOpticSwitchForRun);
        return true;
    }

    private HardwareConfig BuildCalibrationConfigFromUi(int channel, int calibrationCurrent)
    {
        HardwareConfig cfg = BuildBaseConfigForChannel(channel);
        int sensorCount = _loadedCoefficientProfile?.SensorPositionsRaw.Length ?? DefaultSensorCount;

        cfg.Channel = channel;
        cfg.CoefficientFilePath = string.Empty;
        cfg.EdfaCurrentMa = calibrationCurrent;
        cfg.EdfaPaCurrentMa = ParseInt(EdfaPaCurrentTextBox.Text, cfg.EdfaPaCurrentMa);
        cfg.CalibrationEdfaCurrentMa = calibrationCurrent;
        cfg.CalibrationEdfaPaCurrentMa = cfg.EdfaPaCurrentMa;
        cfg.SensorPositionsM = BuildUniformSensorPositionsRaw(cfg.FiberLengthM, 0.0f, sensorCount);
        cfg.SensorWaveIndexes = BuildWaveIndexes(cfg.SensorPositionsM.Length, cfg.CenterWavelengths.Length);
        cfg.SensorPositionScaleToMeters = SensorRawPositionScaleToMeters;
        cfg.SensorTempSensitivityPmPerC = Array.Empty<float>();
        cfg.SensorStrainSensitivity = Array.Empty<float>();
        cfg.SensorReferenceTemperaturesC = Array.Empty<float>();
        cfg.SensorReferenceStrains = Array.Empty<float>();
        cfg.SensorReferenceWavelengthsNm = Array.Empty<float>();
        cfg.SensorReferenceStrainWavelengthsNm = Array.Empty<float>();
        return cfg;
    }

    private static int CalcProfilePointsByStep(int fiberLengthM, int stepM)
    {
        int safeLength = Math.Max(1, fiberLengthM);
        int safeStep = Math.Max(1, stepM);
        return Math.Max(MinProfilePoints, (safeLength / safeStep) + 1);
    }

    private static int[] BuildUniformSensorPositionsRaw(int fiberLengthM, float startLengthM, int count)
    {
        count = Math.Max(2, count);
        int[] arr = new int[count];
        int fiberLengthRaw = Math.Max(10, fiberLengthM * 10);
        int startOffsetRaw = Math.Max(0, (int)Math.Round(Math.Max(0.0f, startLengthM) * 10.0f));
        int endPositionRaw = Math.Max(startOffsetRaw + 1, fiberLengthRaw);
        int effectiveLengthRaw = Math.Max(1, endPositionRaw - startOffsetRaw);
        for (int i = 0; i < count; i++)
        {
            arr[i] = startOffsetRaw + (int)Math.Round(i * effectiveLengthRaw / (double)(count - 1));
        }
        return arr;
    }

    private static int[] BuildWaveIndexes(int sensorCount, int waveCount)
    {
        int[] arr = new int[sensorCount];
        for (int i = 0; i < sensorCount; i++)
        {
            arr[i] = waveCount == 0 ? 0 : (i % waveCount);
        }
        return arr;
    }

    private static int[] BuildDisplayWaveIndexes(int sensorCount, int waveCount)
    {
        return BuildWaveIndexes(sensorCount, waveCount)
            .Select(ToDisplayWaveIndex)
            .ToArray();
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static float ParseFloat(string text, float fallback)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    private string BuildConfigFingerprint(HardwareConfig config)
    {
        return JsonSerializer.Serialize(new
        {
            config.StartWavelengthNm,
            config.StopWavelengthNm,
            config.FiberLengthM,
            config.DelayNs,
            config.PulseWidth,
            config.TargetProfilePoints,
            config.OpticSwitchEnabled,
            config.EdfaCurrentMa,
            config.EdfaPaCurrentMa,
            config.FiberDensityMode,
            config.MultiWaveReverse,
            config.SpeedMode,
            config.LaserType,
            config.AlgorithmType,
            config.Channel,
            config.ChannelEnabled,
            config.SensorPositionScaleToMeters,
            config.CenterWavelengths,
            config.SensorPositionsM,
            config.SensorWaveIndexes,
            config.ReferenceTemperatureC,
            config.WavelengthSensitivityNmPerC,
            config.SensorTempSensitivityPmPerC,
            config.SensorStrainSensitivity,
            config.SensorReferenceTemperaturesC,
            config.SensorReferenceStrains,
            config.SensorReferenceWavelengthsNm,
            config.SensorReferenceStrainWavelengthsNm
        });
    }

    private void InvalidateAppliedHardwareConfigCache()
    {
        _appliedHardwareConfigFingerprintsByChannel.Clear();
        _appliedAllChannelsLowSpeedMode = null;
    }

    private bool DoesAppliedConfigMatch(int channel, HardwareConfig config, bool allChannelsLowSpeed)
    {
        if (_appliedAllChannelsLowSpeedMode != allChannelsLowSpeed)
        {
            return false;
        }

        return _appliedHardwareConfigFingerprintsByChannel.TryGetValue(channel, out string? appliedFingerprint) &&
               string.Equals(appliedFingerprint, BuildConfigFingerprint(config), StringComparison.Ordinal);
    }

    private bool IsMultiChannelRunEnabled(IReadOnlyCollection<int>? enabledChannels = null)
    {
        enabledChannels ??= GetEnabledParameterChannelIndexes();
        return OpticSwitchEnabledCheckBox?.IsChecked == true &&
               GetSelectedMonitorOption()?.IsAllChannels == true &&
               enabledChannels.Count > 1;
    }

    private bool TryBuildCurrentConfigForHardwareAction(out HardwareConfig config, out bool allChannelsLowSpeed, out string reason)
    {
        config = new HardwareConfig();
        allChannelsLowSpeed = false;
        reason = string.Empty;

        if (_service is null)
        {
            reason = "监控服务尚未初始化。";
            return false;
        }

        if (!ValidateChannelSelectionBeforeSave(showMessageBox: false, out reason))
        {
            return false;
        }

        List<int> enabledChannels = GetEnabledParameterChannelIndexes();
        allChannelsLowSpeed = IsMultiChannelRunEnabled(enabledChannels);
        int selectedChannel = Math.Clamp(GetSelectedMonitorChannelIndex(enabledChannels[0]), 0, MaxMonitorChannels - 1);
        int activeRunChannel = enabledChannels.Contains(selectedChannel)
            ? selectedChannel
            : enabledChannels[0];
        IReadOnlyList<int> channelsToValidate = allChannelsLowSpeed
            ? _parameterChannelSettings.Select(x => x.ChannelIndex).OrderBy(x => x).ToArray()
            : new[] { activeRunChannel };

        if (!TryBuildConfigForChannel(activeRunChannel, allChannelsLowSpeed, requireCoefficientProfile: true, out config, out reason))
        {
            return false;
        }

        foreach (int channel in channelsToValidate)
        {
            bool requireCoefficientProfile = enabledChannels.Contains(channel);
            if (!TryBuildConfigForChannel(channel, allChannelsLowSpeed, requireCoefficientProfile, out HardwareConfig channelConfig, out reason))
            {
                return false;
            }

            if (!DoesAppliedConfigMatch(channel, channelConfig, allChannelsLowSpeed))
            {
                reason = "当前参数或系数已变更，请重新下发配置。";
                return false;
            }
        }

        return true;
    }

    private bool TryEnsureCurrentConfigForHardwareAction(out HardwareConfig config, out bool allChannelsLowSpeed, out string reason)
    {
        if (TryBuildCurrentConfigForHardwareAction(out config, out allChannelsLowSpeed, out reason))
        {
            return true;
        }

        if (!string.Equals(reason, "当前参数或系数已变更，请重新下发配置。", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string applyError))
        {
            reason = string.IsNullOrWhiteSpace(applyError)
                ? "当前参数自动同步失败。"
                : $"当前参数自动同步失败：{applyError}";
            allChannelsLowSpeed = false;
            return false;
        }

        if (TryBuildCurrentConfigForHardwareAction(out config, out allChannelsLowSpeed, out reason))
        {
            AddRuntimeLog("运行前已自动同步当前配置。");
            return true;
        }

        return false;
    }

    private bool TryApplyCurrentConfig(bool showMessageBox, bool showSuccessMessage, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (_service is null)
        {
            errorMessage = "监控服务尚未初始化。";
            return false;
        }

        if (!ValidateChannelSelectionBeforeSave(showMessageBox, out errorMessage))
        {
            return false;
        }

        if (_service.GetState() == 5)
        {
            errorMessage = "设备当前仍处于校准中，请先停止校准，再下发配置。";
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "配置", errorMessage);
            }
            return false;
        }

        List<int> enabledChannels = GetEnabledParameterChannelIndexes();
        bool applyAllRunChannels = IsMultiChannelRunEnabled(enabledChannels);
        int selectedChannel = Math.Clamp(GetSelectedMonitorChannelIndex(), 0, MaxMonitorChannels - 1);
        int singleChannelToApply = enabledChannels.Contains(selectedChannel)
            ? selectedChannel
            : enabledChannels[0];
        IReadOnlyList<int> channelsToApply = applyAllRunChannels
            ? _parameterChannelSettings.Select(x => x.ChannelIndex).OrderBy(x => x).ToArray()
            : new[] { singleChannelToApply };

        if (!applyAllRunChannels &&
            !TryGetCoefficientProfileForChannel(singleChannelToApply, requireProfile: true, out _, out errorMessage))
        {
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "配置", errorMessage);
            }
            return false;
        }

        var appliedFingerprints = new Dictionary<int, string>();
        HardwareConfig? selectedConfig = null;
        foreach (int channel in channelsToApply)
        {
            bool requireCoefficientProfile = enabledChannels.Contains(channel);
            if (!TryBuildConfigForChannel(channel, applyAllRunChannels, requireCoefficientProfile, out HardwareConfig channelConfig, out errorMessage))
            {
                if (showMessageBox)
                {
                    AppMessageDialog.ShowInfo(this, "配置", errorMessage);
                }

                return false;
            }

            int rc = _service.ApplyConfig(channelConfig);
            if (rc != 0)
            {
                errorMessage = $"{FormatChannelLabel(channel)} {BuildFriendlyHardwareError("同步参数到设备", rc)}";
                if (showMessageBox)
                {
                    AppMessageDialog.ShowInfo(this, "配置", errorMessage);
                }
                AddRuntimeLog($"参数同步失败：{FormatChannelLabel(channel)} rc={rc}");
                return false;
            }

            appliedFingerprints[channel] = BuildConfigFingerprint(channelConfig);
            if (channel == singleChannelToApply)
            {
                selectedConfig = channelConfig;
            }
        }

        _appliedHardwareConfigFingerprintsByChannel.Clear();
        foreach ((int channel, string fingerprint) in appliedFingerprints)
        {
            _appliedHardwareConfigFingerprintsByChannel[channel] = fingerprint;
        }
        _appliedAllChannelsLowSpeedMode = applyAllRunChannels;

        _config = selectedConfig ?? BuildConfigFromUi();
        SaveUiState();
        if (showSuccessMessage)
        {
            AppMessageDialog.ShowInfo(this, "参数", "参数已同步到设备。");
        }
        AddRuntimeLog("参数已同步到设备。");
        return true;
    }

    private bool TryAutoSyncCurrentChannelConfigAfterConnect(out string message)
    {
        message = "未自动同步。";
        if (_service is null)
        {
            return false;
        }

        foreach (int channel in GetEnabledParameterChannelIndexes())
        {
            if (TryGetCoefficientProfileForChannel(channel, requireProfile: true, out _, out string profileError))
            {
                continue;
            }

            message = profileError;
            return false;
        }

        if (!TryApplyCurrentConfig(showMessageBox: false, showSuccessMessage: false, out string error))
        {
            message = $"自动同步配置失败：{error}";
            return false;
        }

        message = IsMultiChannelRunEnabled()
            ? "已自动同步已勾选通道配置。"
            : "已自动同步当前配置。";
        return true;
    }

    private void SetCoefficientStatus(string text, bool success)
    {
        if (CoefficientStatusTextBlock is null)
        {
            return;
        }

        CoefficientStatusTextBlock.Text = text;
        CoefficientStatusTextBlock.Foreground = success
            ? BrushFromHex("#87D8A4")
            : BrushFromHex("#7FB4F0");
    }

    private void ChannelTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveUiState();
    }

    private void PersistVisibleCoefficientPathForActiveChannel()
    {
        if (_activeCoefficientChannel < 0 || CoefficientFilePathTextBox is null)
        {
            return;
        }

        string path = CoefficientFilePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _coefficientFilePathsByChannel.Remove(_activeCoefficientChannel);
        }
        else if (TryValidateCoefficientFileChannel(path, _activeCoefficientChannel, out _))
        {
            _coefficientFilePathsByChannel[_activeCoefficientChannel] = path;
        }
    }

    private void EnsureCoefficientContextForSelectedMonitorChannel(bool suppressLog = false)
    {
        int targetChannel = GetSelectedMonitorChannelIndex();
        if (targetChannel == _activeCoefficientChannel)
        {
            return;
        }

        SwitchCoefficientContext(targetChannel, suppressLog);
    }

    private void SwitchCoefficientContext(int targetChannel, bool suppressLog)
    {
        PersistVisibleCoefficientPathForActiveChannel();
        _activeCoefficientChannel = targetChannel;

        string path = _coefficientFilePathsByChannel.TryGetValue(targetChannel, out string? storedPath)
            ? storedPath
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(path) && !IsCoefficientFileInAllowedDirectory(path))
        {
            _coefficientFilePathsByChannel.Remove(targetChannel);
            path = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = ResolveAutoCoefficientFilePath(targetChannel);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _coefficientFilePathsByChannel[targetChannel] = path;
            }
        }
        CoefficientFilePathTextBox.Text = path;

        if (_loadedCoefficientProfilesByChannel.TryGetValue(targetChannel, out LoadedCoefficientProfile? cachedProfile))
        {
            _loadedCoefficientProfile = cachedProfile;
            ApplyLoadedCoefficientProfileToUi(cachedProfile, targetChannel, addRuntimeLog: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _loadedCoefficientProfile = null;
            SetCoefficientStatus($"{FormatChannelLabel(targetChannel)} 未自动找到系统系数文件。", false);
            return;
        }

        if (!File.Exists(path))
        {
            _loadedCoefficientProfile = null;
            SetCoefficientStatus($"{FormatChannelLabel(targetChannel)} 已记录系数文件路径，但文件不存在。", false);
            return;
        }

        if (!TryValidateCoefficientFileChannel(path, targetChannel, out string validationMessage))
        {
            _loadedCoefficientProfile = null;
            _loadedCoefficientProfilesByChannel.Remove(targetChannel);
            SetCoefficientStatus(validationMessage, false);
            return;
        }

        try
        {
            LoadedCoefficientProfile profile = LoadCoefficientProfile(path);
            _loadedCoefficientProfilesByChannel[targetChannel] = profile;
            _loadedCoefficientProfile = profile;
            ApplyLoadedCoefficientProfileToUi(profile, targetChannel, addRuntimeLog: !suppressLog);
        }
        catch (Exception ex)
        {
            _loadedCoefficientProfile = null;
            _loadedCoefficientProfilesByChannel.Remove(targetChannel);
            SetCoefficientStatus($"{FormatChannelLabel(targetChannel)} 系数文件自动加载失败：{ex.Message}", false);
        }
    }

    private string ResolveAutoCoefficientFilePath(int targetChannel)
    {
        string candidate = IoPath.Combine(AppDomain.CurrentDomain.BaseDirectory, BuildDeviceCoefficientFileName(targetChannel));
        return File.Exists(candidate) ? candidate : string.Empty;
    }

    private string ResolveCoefficientSavePath(int channel)
    {
        return IoPath.Combine(AppDomain.CurrentDomain.BaseDirectory, BuildDeviceCoefficientFileName(channel));
    }

    private string GetCurrentDeviceExportNamePart()
    {
        string deviceName = _currentDevice?.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = _currentDevice?.Ip?.Trim() ?? string.Empty;
        }

        deviceName = SanitizeFileNamePart(deviceName);
        return string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName;
    }

    private string BuildDeviceCoefficientFileName(int channel)
    {
        string deviceName = _currentDevice?.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = _currentDevice?.Ip?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = "default";
        }

        return $"系统系数文件_{SanitizeFileNamePart(deviceName)}_CH{channel + DisplayChannelBase}.csv";
    }

    private void ApplyLoadedCoefficientProfileToUi(LoadedCoefficientProfile profile, int channel, bool addRuntimeLog)
    {
        _loadedCoefficientProfile = profile;
        InvalidateAppliedHardwareConfigCache();
        CoefficientFilePathTextBox.Text = profile.FilePath;
        if (profile.DisplaySensorPositionsM.Length > 0)
        {
            SetCoefficientStatus(
                $"{FormatChannelLabel(channel)} 已加载系数文件：{profile.SensorPositionsRaw.Length} 个传感器点，位置范围 {profile.DisplaySensorPositionsM.First():F1}-{profile.DisplaySensorPositionsM.Last():F1} m",
                true);
        }
        else
        {
            SetCoefficientStatus($"{FormatChannelLabel(channel)} 已加载系数文件：{profile.SensorPositionsRaw.Length} 个传感器点。", true);
        }

        if (addRuntimeLog)
        {
            AddRuntimeLog($"{FormatChannelLabel(channel)} 系统系数文件加载成功：{IoPath.GetFileName(profile.FilePath)}");
        }
    }

    private LoadedCoefficientProfile BuildCoefficientProfileFromCalibration(CalibrationResultModel calibration, HardwareConfig cfg)
    {
        const float coefficientPositionScaleToMeters = 0.1f;

        int sensorCount = calibration.SensorPositionsRaw.Length;
        string currentPath = CoefficientFilePathTextBox.Text.Trim();
        string filePath = TryValidateCoefficientFileChannel(currentPath, calibration.Channel, out _)
            ? currentPath
            : string.Empty;
        int[] absolutePositionsRaw = calibration.SensorPositionsRaw.ToArray();

        var profile = new LoadedCoefficientProfile
        {
            FilePath = filePath,
            SourceSensorIndexes = Enumerable.Range(0, sensorCount).ToArray(),
            SensorPositionsRaw = absolutePositionsRaw,
            DisplaySensorPositionsM = absolutePositionsRaw
                .Select(x => x * coefficientPositionScaleToMeters)
                .ToArray(),
            PositionScaleToMeters = coefficientPositionScaleToMeters
        };

        int[] normalizedWaveIndexes = NormalizeWaveIndexes(calibration.SensorWaveIndexesRaw, cfg.CenterWavelengths, cfg.MultiWaveReverse);

        if (_loadedCoefficientProfilesByChannel.TryGetValue(calibration.Channel, out LoadedCoefficientProfile? existingProfile) &&
            existingProfile.SensorPositionsRaw.Length == sensorCount)
        {
            if (string.IsNullOrWhiteSpace(profile.FilePath) &&
                TryValidateCoefficientFileChannel(existingProfile.FilePath, calibration.Channel, out _))
            {
                profile.FilePath = existingProfile.FilePath;
            }

            profile.SensorWaveIndexes = normalizedWaveIndexes.Length == sensorCount
                ? normalizedWaveIndexes
                : existingProfile.SensorWaveIndexes.ToArray();
            profile.SourceSensorIndexes = existingProfile.SourceSensorIndexes.Length == sensorCount
                ? existingProfile.SourceSensorIndexes.ToArray()
                : Enumerable.Range(0, sensorCount).ToArray();
            profile.TempSensitivityPmPerC = existingProfile.TempSensitivityPmPerC.ToArray();
            profile.StrainSensitivity = existingProfile.StrainSensitivity.ToArray();
            profile.ReferenceTemperaturesC = existingProfile.ReferenceTemperaturesC.ToArray();
            profile.ReferenceStrains = existingProfile.ReferenceStrains.ToArray();
            profile.ReferenceWavelengthsNm = existingProfile.ReferenceWavelengthsNm.ToArray();
            profile.ReferenceStrainWavelengthsNm = existingProfile.ReferenceStrainWavelengthsNm.ToArray();
            return profile;
        }

        int waveCount = Math.Max(1, cfg.CenterWavelengths.Length);
        const float defaultReferenceTemperature = 24.0f;

        profile.SensorWaveIndexes = normalizedWaveIndexes.Length == sensorCount
            ? normalizedWaveIndexes
            : BuildDisplayWaveIndexes(sensorCount, waveCount);
        profile.TempSensitivityPmPerC = Enumerable.Repeat(10.8f, sensorCount).ToArray();
        profile.StrainSensitivity = Enumerable.Repeat(1.0f, sensorCount).ToArray();
        profile.ReferenceTemperaturesC = Enumerable.Repeat(defaultReferenceTemperature, sensorCount).ToArray();
        profile.ReferenceStrains = new float[sensorCount];
        profile.ReferenceWavelengthsNm = profile.SensorWaveIndexes
            .Select(index => ResolveCenterWavelengthFromDisplayIndex(cfg.CenterWavelengths, index))
            .ToArray();
        profile.ReferenceStrainWavelengthsNm = profile.SensorWaveIndexes
            .Select(index => ResolveCenterWavelengthFromDisplayIndex(cfg.CenterWavelengths, index))
            .ToArray();
        return profile;
    }

    private static LoadedCoefficientProfile BuildCoefficientProfileFromCalibrationRows(
        LoadedCoefficientProfile baseProfile,
        IReadOnlyList<CalibrationWindow.CalibrationRowItem> rows)
    {
        CalibrationWindow.CalibrationRowItem[] orderedRows = rows
            .OrderBy(x => x.SourceIndex)
            .ThenBy(x => x.SamplePoint)
            .ThenBy(x => x.WaveIndex)
            .ToArray();
        var sourceIndexes = orderedRows.Select(x => x.SourceIndex).ToArray();
        bool canReuseBySourceIndex = sourceIndexes.All(x => x >= 0 && x < baseProfile.SensorPositionsRaw.Length);

        int[] filteredWaveIndexes = orderedRows.Select(x => x.WaveIndex).ToArray();
        int[] filteredPositionsRaw = orderedRows.Select(x => x.SamplePoint).ToArray();
        float[] filteredPositionsM = orderedRows.Select(x => x.PositionM).ToArray();

        if (!canReuseBySourceIndex)
        {
            Dictionary<int, float> temperatureReferenceByWaveIndex = BuildWaveIndexFloatMap(baseProfile.SensorWaveIndexes, baseProfile.ReferenceWavelengthsNm);
            Dictionary<int, float> strainReferenceByWaveIndex = BuildWaveIndexFloatMap(baseProfile.SensorWaveIndexes, baseProfile.ReferenceStrainWavelengthsNm);
            return new LoadedCoefficientProfile
            {
                FilePath = baseProfile.FilePath,
                SourceSensorIndexes = Enumerable.Range(0, rows.Count).ToArray(),
                SensorPositionsRaw = filteredPositionsRaw,
                DisplaySensorPositionsM = filteredPositionsM,
                PositionScaleToMeters = baseProfile.PositionScaleToMeters,
                SensorWaveIndexes = filteredWaveIndexes,
                TempSensitivityPmPerC = Enumerable.Range(0, rows.Count)
                    .Select(i => i < baseProfile.TempSensitivityPmPerC.Length && baseProfile.TempSensitivityPmPerC[i] > 0
                        ? baseProfile.TempSensitivityPmPerC[i]
                        : 10.8f)
                    .ToArray(),
                StrainSensitivity = Enumerable.Range(0, rows.Count)
                    .Select(i => i < baseProfile.StrainSensitivity.Length && baseProfile.StrainSensitivity[i] > 0
                        ? baseProfile.StrainSensitivity[i]
                        : 1.0f)
                    .ToArray(),
                ReferenceTemperaturesC = Enumerable.Range(0, rows.Count)
                    .Select(i => i < baseProfile.ReferenceTemperaturesC.Length && float.IsFinite(baseProfile.ReferenceTemperaturesC[i])
                        ? baseProfile.ReferenceTemperaturesC[i]
                        : 24.0f)
                    .ToArray(),
                ReferenceStrains = Enumerable.Range(0, rows.Count)
                    .Select(i => i < baseProfile.ReferenceStrains.Length && float.IsFinite(baseProfile.ReferenceStrains[i])
                        ? baseProfile.ReferenceStrains[i]
                        : 0.0f)
                    .ToArray(),
                ReferenceWavelengthsNm = filteredWaveIndexes
                    .Select(index => ResolveReferenceWavelengthForWaveIndex(index, temperatureReferenceByWaveIndex))
                    .ToArray(),
                ReferenceStrainWavelengthsNm = filteredWaveIndexes
                    .Select(index => ResolveReferenceWavelengthForWaveIndex(index, strainReferenceByWaveIndex))
                    .ToArray()
            };
        }

        return new LoadedCoefficientProfile
        {
            FilePath = baseProfile.FilePath,
            SourceSensorIndexes = Enumerable.Range(0, rows.Count).ToArray(),
            SensorPositionsRaw = filteredPositionsRaw,
            DisplaySensorPositionsM = filteredPositionsM,
            PositionScaleToMeters = baseProfile.PositionScaleToMeters,
            SensorWaveIndexes = filteredWaveIndexes,
            TempSensitivityPmPerC = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.TempSensitivityPmPerC, i)).ToArray(),
            StrainSensitivity = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.StrainSensitivity, i)).ToArray(),
            ReferenceTemperaturesC = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.ReferenceTemperaturesC, i)).ToArray(),
            ReferenceStrains = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.ReferenceStrains, i)).ToArray(),
            ReferenceWavelengthsNm = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.ReferenceWavelengthsNm, i)).ToArray(),
            ReferenceStrainWavelengthsNm = sourceIndexes.Select(i => GetProfileFloatValue(baseProfile.ReferenceStrainWavelengthsNm, i)).ToArray()
        };
    }

    private static float ResolveCenterWavelength(float[] centerWavelengths, int waveIndex)
    {
        if (centerWavelengths.Length == 0)
        {
            return 1550.0f;
        }

        int resolvedIndex = Math.Clamp(waveIndex, 0, centerWavelengths.Length - 1);
        return centerWavelengths[resolvedIndex];
    }

    private static float ResolveCenterWavelengthFromDisplayIndex(float[] centerWavelengths, int displayWaveIndex)
    {
        return ResolveCenterWavelength(centerWavelengths, displayWaveIndex - 1);
    }

    private static int[] NormalizeWaveIndexes(int[] rawWaveIndexes, float[] centerWavelengths, bool multiWaveReverse)
    {
        if (rawWaveIndexes.Length == 0)
        {
            return Array.Empty<int>();
        }

        int waveCount = Math.Max(1, centerWavelengths.Length);
        bool rawWaveIndexesAreOneBased = AreWaveIndexesOneBased(rawWaveIndexes, waveCount);
        return rawWaveIndexes
            .Select(index =>
            {
                int rawIndex = rawWaveIndexesAreOneBased ? index - 1 : index;
                int normalized = rawIndex < 0 ? 0 : Math.Clamp(rawIndex, 0, waveCount - 1);
                int configIndex = multiWaveReverse ? waveCount - 1 - normalized : normalized;
                return ToDisplayWaveIndex(configIndex);
            })
            .ToArray();
    }

    private static bool AreWaveIndexesOneBased(int[] rawWaveIndexes, int waveCount)
    {
        if (waveCount <= 0 || rawWaveIndexes.Length == 0)
        {
            return false;
        }

        int[] valid = rawWaveIndexes.Where(x => x >= 0).ToArray();
        return valid.Length > 0 &&
               valid.All(x => x >= 1 && x <= waveCount) &&
               valid.Any(x => x == waveCount) &&
               !valid.Any(x => x == 0);
    }

    private static float ResolveConfiguredCenterWavelength(float[] centerWavelengths, int rawWaveIndex, bool multiWaveReverse)
    {
        if (centerWavelengths.Length == 0)
        {
            return 1550.0f;
        }

        int resolvedIndex = Math.Clamp(rawWaveIndex, 0, centerWavelengths.Length - 1);
        if (multiWaveReverse)
        {
            resolvedIndex = centerWavelengths.Length - 1 - resolvedIndex;
        }

        return centerWavelengths[resolvedIndex];
    }

    private static void SaveCoefficientProfile(string path, LoadedCoefficientProfile profile)
    {
        string? directory = IoPath.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int sensorCount = profile.SensorPositionsRaw.Length;
        var builder = new StringBuilder();
        builder.AppendLine("\u5149\u6805\u7f16\u53f7,\u5149\u6805\u4f4d\u7f6e,\u6ce2\u957f\u5e8f\u53f7,\u6e29\u654f\u7cfb\u6570,\u5e94\u53d8\u7cfb\u6570,\u6e29\u5ea6\u57fa\u51c6\u503c,\u5e94\u53d8\u57fa\u51c6\u503c,\u6e29\u5ea6\u6ce2\u957f\u57fa\u51c6\u503c,\u5e94\u53d8\u6ce2\u957f\u57fa\u51c6\u503c");

        for (int i = 0; i < sensorCount; i++)
        {
            builder
                .Append(i + 1).Append(',')
                .Append(profile.SensorPositionsRaw[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileIntValue(profile.SensorWaveIndexes, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.TempSensitivityPmPerC, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.StrainSensitivity, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.ReferenceTemperaturesC, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.ReferenceStrains, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.ReferenceWavelengthsNm, i).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetProfileFloatValue(profile.ReferenceStrainWavelengthsNm, i).ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static int GetProfileIntValue(int[] values, int index)
    {
        return index < values.Length ? values[index] : 0;
    }

    private static float GetProfileFloatValue(float[] values, int index)
    {
        return index < values.Length ? values[index] : 0.0f;
    }

    private static Dictionary<int, float> BuildWaveIndexFloatMap(int[] waveIndexes, float[] values)
    {
        var result = new Dictionary<int, float>();
        int count = Math.Min(waveIndexes.Length, values.Length);
        for (int i = 0; i < count; i++)
        {
            float value = values[i];
            if (!float.IsFinite(value) || value <= 0)
            {
                continue;
            }

            result.TryAdd(waveIndexes[i], value);
        }

        return result;
    }

    private static float ResolveReferenceWavelengthForWaveIndex(int waveIndex, IReadOnlyDictionary<int, float> valuesByWaveIndex)
    {
        return valuesByWaveIndex.TryGetValue(waveIndex, out float value) && float.IsFinite(value) && value > 0
            ? value
            : 0.0f;
    }

    private static void RepairIncompleteCoefficientRows(LoadedCoefficientProfile profile)
    {
        Dictionary<int, float> temperatureReferenceByWaveIndex = BuildWaveIndexFloatMap(profile.SensorWaveIndexes, profile.ReferenceWavelengthsNm);
        Dictionary<int, float> strainReferenceByWaveIndex = BuildWaveIndexFloatMap(profile.SensorWaveIndexes, profile.ReferenceStrainWavelengthsNm);
        int sensorCount = profile.SensorPositionsRaw.Length;
        float[] tempSensitivity = profile.TempSensitivityPmPerC;
        float[] strainSensitivity = profile.StrainSensitivity;
        float[] referenceTemperatures = profile.ReferenceTemperaturesC;
        float[] referenceStrains = profile.ReferenceStrains;
        float[] referenceWavelengths = profile.ReferenceWavelengthsNm;
        float[] referenceStrainWavelengths = profile.ReferenceStrainWavelengthsNm;
        for (int i = 0; i < sensorCount; i++)
        {
            if (i >= tempSensitivity.Length || tempSensitivity[i] <= 0)
            {
                EnsureFloatArrayLength(ref tempSensitivity, sensorCount);
                tempSensitivity[i] = 10.8f;
            }

            if (i >= strainSensitivity.Length || strainSensitivity[i] <= 0)
            {
                EnsureFloatArrayLength(ref strainSensitivity, sensorCount);
                strainSensitivity[i] = 1.0f;
            }

            if (i >= referenceTemperatures.Length)
            {
                EnsureFloatArrayLength(ref referenceTemperatures, sensorCount);
                referenceTemperatures[i] = 24.0f;
            }

            if (i >= referenceStrains.Length)
            {
                EnsureFloatArrayLength(ref referenceStrains, sensorCount);
                referenceStrains[i] = 0.0f;
            }

            if (i >= referenceWavelengths.Length || referenceWavelengths[i] <= 0)
            {
                EnsureFloatArrayLength(ref referenceWavelengths, sensorCount);
                int waveIndex = i < profile.SensorWaveIndexes.Length ? profile.SensorWaveIndexes[i] : 0;
                referenceWavelengths[i] = ResolveReferenceWavelengthForWaveIndex(waveIndex, temperatureReferenceByWaveIndex);
            }

            if (i >= referenceStrainWavelengths.Length || referenceStrainWavelengths[i] <= 0)
            {
                EnsureFloatArrayLength(ref referenceStrainWavelengths, sensorCount);
                int waveIndex = i < profile.SensorWaveIndexes.Length ? profile.SensorWaveIndexes[i] : 0;
                referenceStrainWavelengths[i] = ResolveReferenceWavelengthForWaveIndex(waveIndex, strainReferenceByWaveIndex);
            }
        }

        profile.TempSensitivityPmPerC = tempSensitivity;
        profile.StrainSensitivity = strainSensitivity;
        profile.ReferenceTemperaturesC = referenceTemperatures;
        profile.ReferenceStrains = referenceStrains;
        profile.ReferenceWavelengthsNm = referenceWavelengths;
        profile.ReferenceStrainWavelengthsNm = referenceStrainWavelengths;
    }

    private static void EnsureFloatArrayLength(ref float[] values, int requiredLength)
    {
        if (values.Length < requiredLength)
        {
            Array.Resize(ref values, requiredLength);
        }
    }

    private static LoadedCoefficientProfile LoadCoefficientProfile(string path)
    {
        const float coefficientPositionScaleToMeters = 0.1f;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("系数文件不存在。", path);
        }

        string[] lines = ReadCoefficientFileLines(path);
        if (lines.Length <= 1)
        {
            throw new InvalidDataException("系数文件没有有效数据。");
        }

        var positionsRaw = new List<int>();
        var displayPositions = new List<float>();
        var waveIndexes = new List<int>();
        var sensitivities = new List<float>();
        var strainSensitivities = new List<float>();
        var referenceTemps = new List<float>();
        var referenceStrains = new List<float>();
        var referenceWavelengths = new List<float>();
        var referenceStrainWavelengths = new List<float>();

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(',', StringSplitOptions.None);
            if (parts.Length < 9)
            {
                continue;
            }

            int rawPosition = ParseRequiredInt(parts[1], $"第 {i + 1} 行的光栅位置无效");
            positionsRaw.Add(rawPosition);
            displayPositions.Add(rawPosition * coefficientPositionScaleToMeters);
            waveIndexes.Add(ParseRequiredInt(parts[2], $"第 {i + 1} 行的波长序号无效"));
            sensitivities.Add(ParseRequiredFloat(parts[3], $"第 {i + 1} 行的温敏系数无效"));
            strainSensitivities.Add(ParseRequiredFloat(parts[4], $"第 {i + 1} 行的应变系数无效"));
            referenceTemps.Add(ParseRequiredFloat(parts[5], $"第 {i + 1} 行的温度基准值无效"));
            referenceStrains.Add(ParseRequiredFloat(parts[6], $"第 {i + 1} 行的应变基准值无效"));
            referenceWavelengths.Add(ParseRequiredFloat(parts[7], $"第 {i + 1} 行的温度波长基准值无效"));
            referenceStrainWavelengths.Add(ParseRequiredFloat(parts[8], $"第 {i + 1} 行的应变波长基准值无效"));
        }

        if (positionsRaw.Count == 0)
        {
            throw new InvalidDataException("系数文件中没有解析到任何传感器记录。");
        }

        var profile = new LoadedCoefficientProfile
        {
            FilePath = path,
            SourceSensorIndexes = Enumerable.Range(0, positionsRaw.Count).ToArray(),
            SensorPositionsRaw = positionsRaw.ToArray(),
            DisplaySensorPositionsM = displayPositions.ToArray(),
            PositionScaleToMeters = coefficientPositionScaleToMeters,
            SensorWaveIndexes = waveIndexes.ToArray(),
            TempSensitivityPmPerC = sensitivities.ToArray(),
            StrainSensitivity = strainSensitivities.ToArray(),
            ReferenceTemperaturesC = referenceTemps.ToArray(),
            ReferenceStrains = referenceStrains.ToArray(),
            ReferenceWavelengthsNm = referenceWavelengths.ToArray(),
            ReferenceStrainWavelengthsNm = referenceStrainWavelengths.ToArray()
        };
        RepairIncompleteCoefficientRows(profile);
        return profile;
    }

    private static int ParseRequiredInt(string text, string error)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException(error);
        }

        return value;
    }

    private static float ParseRequiredFloat(string text, string error)
    {
        if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            throw new InvalidDataException(error);
        }

        return value;
    }

    private static string[] ReadCoefficientFileLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }

        string[] utf8Lines = File.ReadAllLines(path, Encoding.UTF8);
        if (utf8Lines.Any(line => line.Contains('\uFFFD')))
        {
            return File.ReadAllLines(path, Encoding.Default);
        }

        return utf8Lines;
    }

    private static float[] ParseFloatArray(string text, float[] fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string[] parts = text.Split(
            new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<float>();
        foreach (string part in parts)
        {
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                result.Add(value);
            }
        }

        return result.Count > 0 ? result.ToArray() : fallback;
    }

    private SensorInfoDisplayMode GetSensorInfoDisplayMode()
    {
        return GraphViewTabControl?.SelectedIndex switch
        {
            1 => SensorInfoDisplayMode.Temperature,
            2 => SensorInfoDisplayMode.Strain,
            _ => SensorInfoDisplayMode.Wavelength
        };
    }

    private void UpdateSensorInfoValueColumnHeader()
    {
        if (SensorInfoGrid.Columns.Count < 3)
        {
            return;
        }

        SensorInfoGrid.Columns[2].Header = GetSensorInfoDisplayMode() switch
        {
            SensorInfoDisplayMode.Temperature => "温度(℃)",
            SensorInfoDisplayMode.Strain => "应变(με)",
            _ => "波长(nm)"
        };
    }

    private void UpdateGraphModeButtons()
    {
        int selectedIndex = GraphViewTabControl.SelectedIndex;
        ApplyGraphModeButtonState(WavelengthModeButton, selectedIndex == 0);
        ApplyGraphModeButtonState(TemperatureModeButton, selectedIndex == 1);
        ApplyGraphModeButtonState(ShapeMonitorButton, selectedIndex == 2);
    }

    private void EnsureGraphViewSelection()
    {
        if (GraphViewTabControl.Items.Count == 0)
        {
            return;
        }

        if (GraphViewTabControl.SelectedIndex < 0 || GraphViewTabControl.SelectedIndex >= GraphViewTabControl.Items.Count)
        {
            GraphViewTabControl.SelectedIndex = 0;
        }
    }

    private void ApplyCurrentGraphViewState(bool redrawCharts)
    {
        EnsureGraphViewSelection();
        UpdateGraphModeButtons();
        UpdateSensorInfoValueColumnHeader();
        UpdateRealtimeFrequencyDisplay();
        SnapshotModel? snapshot = ResolveSelectedSnapshot();
        if (snapshot is not null)
        {
            RefreshSensorOptions(snapshot);
        }
        else if (!RefreshSensorOptionsFromCoefficientProfile(preserveScroll: true, ensureSelectedRowVisible: false))
        {
            ClearSensorOptions();
        }

        if (redrawCharts)
        {
            RedrawSelectedChannelViews();
        }
    }

    private void ApplyGraphModeButtonState(Button button, bool isSelected)
    {
        button.Background = isSelected ? BrushFromHex("#1D4F86") : BrushFromHex("#0B2243");
        button.BorderBrush = isSelected ? BrushFromHex("#7EB7FF") : BrushFromHex("#2E5A8F");
        button.Foreground = BrushFromHex("#E9F5FF");
    }

    private static bool TryParseUiFloat(string? text, out float value)
    {
        return float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.GetCultureInfo("zh-CN"), out value);
    }

    private void UpdateRealtimeFrequencyEstimate(SnapshotModel snapshot)
    {
        if (_lastSnapshotTimestampMsOverall > 0 &&
            snapshot.TimestampMs > _lastSnapshotTimestampMsOverall)
        {
            double overallHz = 1000d / (snapshot.TimestampMs - _lastSnapshotTimestampMsOverall);
            if (double.IsFinite(overallHz) && overallHz > 0)
            {
                if (_snapshotFrequencyHzOverall > 0)
                {
                    overallHz = (_snapshotFrequencyHzOverall * 0.45d) + (overallHz * 0.55d);
                }

                _snapshotFrequencyHzOverall = overallHz;
            }
        }

        _lastSnapshotTimestampMsOverall = snapshot.TimestampMs;

        if (_lastSnapshotTimestampMsByChannel.TryGetValue(snapshot.Channel, out long previousTs) &&
            snapshot.TimestampMs > previousTs)
        {
            double instantHz = 1000d / (snapshot.TimestampMs - previousTs);
            if (double.IsFinite(instantHz) && instantHz > 0)
            {
                if (_snapshotFrequencyHzByChannel.TryGetValue(snapshot.Channel, out double previousHz) && previousHz > 0)
                {
                    instantHz = (previousHz * 0.45d) + (instantHz * 0.55d);
                }

                _snapshotFrequencyHzByChannel[snapshot.Channel] = instantHz;
            }
        }

        _lastSnapshotTimestampMsByChannel[snapshot.Channel] = snapshot.TimestampMs;
    }

    private void UpdateRealtimeFrequencyDisplay()
    {
        string text;
        if (GetSelectedMonitorOption()?.IsAllChannels == true)
        {
            text = double.IsFinite(_snapshotFrequencyHzOverall) && _snapshotFrequencyHzOverall > 0
                ? $"{_snapshotFrequencyHzOverall:F4}Hz"
                : "--";
        }
        else
        {
            int channel = GetSelectedMonitorChannelIndex();
            text = _snapshotFrequencyHzByChannel.TryGetValue(channel, out double hz) && double.IsFinite(hz) && hz > 0
                ? $"{hz:F4}Hz"
                : "--";
        }

        if (RealtimeFrequencyTextBlock is not null)
        {
            RealtimeFrequencyTextBlock.Text = text;
        }
    }

    private bool TryGetTemperatureAxisRangeFromUi(out float? min, out float? max, out string error)
    {
        min = null;
        max = null;
        error = string.Empty;

        string minText = TemperatureAxisMinTextBox.Text.Trim();
        string maxText = TemperatureAxisMaxTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(minText) && string.IsNullOrWhiteSpace(maxText))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(minText) || string.IsNullOrWhiteSpace(maxText))
        {
            error = "请同时输入温度曲线纵轴的最小值和最大值。";
            return false;
        }

        if (!TryParseUiFloat(minText, out float minValue) || !float.IsFinite(minValue))
        {
            error = "温度曲线纵轴最小值无效。";
            return false;
        }

        if (!TryParseUiFloat(maxText, out float maxValue) || !float.IsFinite(maxValue))
        {
            error = "温度曲线纵轴最大值无效。";
            return false;
        }

        if (maxValue <= minValue)
        {
            error = "温度曲线纵轴最大值必须大于最小值。";
            return false;
        }

        min = minValue;
        max = maxValue;
        return true;
    }

    private bool TryGetBaselineTemperatureRangeFromUi(out float startM, out float endM, out float baselineTemperatureC, out string error)
    {
        startM = 0f;
        endM = 0f;
        baselineTemperatureC = 0f;
        error = string.Empty;

        string startText = BaselineTemperatureRangeStartTextBox.Text.Trim();
        string endText = BaselineTemperatureRangeEndTextBox.Text.Trim();
        string baselineText = BaselineTemperatureValueTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(startText) ||
            string.IsNullOrWhiteSpace(endText) ||
            string.IsNullOrWhiteSpace(baselineText))
        {
            error = "请完整输入起始位置、结束位置和基准温度。";
            return false;
        }

        if (!TryParseUiFloat(startText, out startM) || !float.IsFinite(startM))
        {
            error = "基准温度范围的起始位置无效。";
            return false;
        }

        if (!TryParseUiFloat(endText, out endM) || !float.IsFinite(endM))
        {
            error = "基准温度范围的结束位置无效。";
            return false;
        }

        if (!TryParseUiFloat(baselineText, out baselineTemperatureC) || !float.IsFinite(baselineTemperatureC))
        {
            error = "基准温度值无效。";
            return false;
        }

        return true;
    }

    private void RestoreTemperatureAxisRangeFromUiState()
    {
        if (TemperatureAxisMinTextBox is null || TemperatureAxisMaxTextBox is null)
        {
            _temperatureAxisMinOverride = null;
            _temperatureAxisMaxOverride = null;
            return;
        }

        if (TryGetTemperatureAxisRangeFromUi(out float? min, out float? max, out _))
        {
            _temperatureAxisMinOverride = min;
            _temperatureAxisMaxOverride = max;
        }
        else
        {
            _temperatureAxisMinOverride = null;
            _temperatureAxisMaxOverride = null;
        }
    }

    private void ApplyTemperatureAxisViewport(float minY, float maxY)
    {
        if (_temperatureChartData is not null &&
            TryGetVisibleDataRange(
                _temperatureChartData,
                _temperatureViewport,
                out _,
                out _,
                out _,
                out _,
                out float minX,
                out float maxX,
                out _,
                out _))
        {
            _temperatureViewport.Set(minX, maxX, minY, maxY);
        }
        else
        {
            _temperatureViewport.Reset();
        }
    }

    private void ApplyTemperatureAxisRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTemperatureAxisRangeFromUi(out float? min, out float? max, out string error))
        {
            AppMessageDialog.ShowInfo(this, "温度曲线纵轴", error);
            return;
        }

        _temperatureAxisMinOverride = min;
        _temperatureAxisMaxOverride = max;
        if (min.HasValue && max.HasValue)
        {
            ApplyTemperatureAxisViewport(min.Value, max.Value);
            AddRuntimeLog($"温度曲线纵轴范围已设置为 {min.Value:F2} ~ {max.Value:F2} ℃");
        }
        else
        {
            _temperatureViewport.Reset();
            AddRuntimeLog("温度曲线纵轴范围已恢复为自动。");
        }

        SaveUiState();
        SetTemperatureAxisRangePanelOpen(false);
        RedrawSelectedChannelViews();
    }

    private void ResetTemperatureAxisRangeButton_Click(object sender, RoutedEventArgs e)
    {
        TemperatureAxisMinTextBox.Text = string.Empty;
        TemperatureAxisMaxTextBox.Text = string.Empty;
        _temperatureAxisMinOverride = null;
        _temperatureAxisMaxOverride = null;
        _temperatureViewport.Reset();
        SaveUiState();
        AddRuntimeLog("温度曲线纵轴范围已重置为自动。");
        SetTemperatureAxisRangePanelOpen(false);
        RedrawSelectedChannelViews();
    }

    private void ToggleTemperatureAxisRangePanelButton_Click(object sender, RoutedEventArgs e)
    {
        bool isOpen = TemperatureAxisRangePopup is not null && !TemperatureAxisRangePopup.IsOpen;
        SetTemperatureAxisRangePanelOpen(isOpen);
    }

    private void SetTemperatureAxisRangePanelOpen(bool isOpen)
    {
        if (TemperatureAxisRangePopup is null || TemperatureAxisToggleButton is null)
        {
            return;
        }

        TemperatureAxisRangePopup.IsOpen = isOpen;
        TemperatureAxisToggleButton.Background = isOpen ? BrushFromHex("#1D4F86") : BrushFromHex("#0B2243");
        TemperatureAxisToggleButton.BorderBrush = isOpen ? BrushFromHex("#7EB7FF") : BrushFromHex("#2E5A8F");
    }

    private void TemperatureAxisRangePopup_Closed(object sender, EventArgs e)
    {
        SetTemperatureAxisRangePanelOpen(false);
    }

    private void RestoreParameterChannelSettings(UiStateSnapshot state)
    {
        foreach (ParameterChannelSettingItem item in _parameterChannelSettings)
        {
            item.IsEnabled = state.ChannelEnabledByChannel is not null &&
                             state.ChannelEnabledByChannel.TryGetValue(item.ChannelIndex, out bool enabled)
                ? enabled
                : true;

            item.CenterWavelengthText = state.CenterWavelengthsByChannel is not null &&
                                        state.CenterWavelengthsByChannel.TryGetValue(item.ChannelIndex, out string? centerText) &&
                                        !string.IsNullOrWhiteSpace(centerText)
                ? centerText
                : string.Empty;
        }
    }

    private void EnsureZoneChannelSelection()
    {
        if (ZoneChannelComboBox.SelectedItem is ChannelOption)
        {
            return;
        }

        string fallbackText = FormatChannelLabel(GetSelectedMonitorChannelIndex());
        SetZoneChannelByText(fallbackText);

        if (ZoneChannelComboBox.SelectedItem is not ChannelOption && ZoneChannelComboBox.Items.Count > 0)
        {
            ZoneChannelComboBox.SelectedIndex = 0;
        }
    }

    private void EnsureAlarmChannelStatesInitialized()
    {
        if (_alarmChannelStatesByChannel.Count > 0)
        {
            return;
        }

        AlarmChannelEditorState seed = CreateAlarmChannelStateFromCurrentUi();
        foreach (ChannelOption option in _channelOptions)
        {
            _alarmChannelStatesByChannel[option.ChannelIndex] = CloneAlarmChannelEditorState(seed);
        }
    }

    private Dictionary<int, AlarmChannelEditorState> GetAlarmEditorStateStore()
    {
        return _isAlarmDialogOpen ? _alarmChannelDraftStatesByChannel : _alarmChannelStatesByChannel;
    }

    private void BeginAlarmDialogSession()
    {
        EnsureAlarmChannelStatesInitialized();
        _alarmChannelDraftStatesByChannel.Clear();
        foreach ((int channel, AlarmChannelEditorState state) in _alarmChannelStatesByChannel)
        {
            _alarmChannelDraftStatesByChannel[channel] = CloneAlarmChannelEditorState(state);
        }

        _isAlarmDialogOpen = true;
    }

    private void EndAlarmDialogSession(bool commit)
    {
        if (commit)
        {
            _alarmChannelStatesByChannel.Clear();
            foreach ((int channel, AlarmChannelEditorState state) in _alarmChannelDraftStatesByChannel)
            {
                _alarmChannelStatesByChannel[channel] = CloneAlarmChannelEditorState(state);
            }
        }

        _alarmChannelDraftStatesByChannel.Clear();
        _isAlarmDialogOpen = false;
    }

    private int GetSelectedZoneChannelIndex()
    {
        if (ZoneChannelComboBox.SelectedItem is ChannelOption option)
        {
            return option.ChannelIndex;
        }

        return GetSelectedMonitorChannelIndex();
    }

    private AlarmChannelEditorState GetOrCreateAlarmChannelState(int channelIndex, out bool created)
    {
        Dictionary<int, AlarmChannelEditorState> store = GetAlarmEditorStateStore();
        channelIndex = Math.Clamp(channelIndex, 0, MaxMonitorChannels - 1);
        if (store.TryGetValue(channelIndex, out AlarmChannelEditorState? state))
        {
            created = false;
            return state;
        }

        state = CreateAlarmChannelStateFromCurrentUi();
        store[channelIndex] = state;
        created = true;
        return state;
    }

    private AlarmChannelEditorState CreateAlarmChannelStateFromCurrentUi()
    {
        return new AlarmChannelEditorState
        {
            EnableAlarmL1 = EnableAlarmL1CheckBox.IsChecked == true,
            EnableDiffAlarm = EnableDiffAlarmCheckBox.IsChecked == true,
            ZoneCount = Math.Clamp(ParseInt(ZoneCountTextBox.Text, 0), 0, 200),
            ZoneLength = Math.Clamp(ParseInt(ZoneLengthTextBox.Text, 0), 0, 100000),
            TempCorrection = ParseFloat(TempCorrectionTextBox.Text, 0f),
            ZoneRows = CloneZoneParameterItems(_zoneParameterItems)
        };
    }

    private static ZoneParameterItem CloneZone(ZoneParameterItem zone)
    {
        return new ZoneParameterItem
        {
            ZoneNo = zone.ZoneNo,
            Description = zone.Description,
            StartPos = zone.StartPos,
            EndPos = zone.EndPos,
            AlarmLevel1 = zone.AlarmLevel1,
            DiffTempAlarm = zone.DiffTempAlarm
        };
    }

    private List<int> GetModifiedAlarmChannelIndices()
    {
        var modified = new List<int>();
        foreach (ChannelOption option in _channelOptions)
        {
            int channel = option.ChannelIndex;
            _alarmChannelStatesByChannel.TryGetValue(channel, out AlarmChannelEditorState? committedState);
            _alarmChannelDraftStatesByChannel.TryGetValue(channel, out AlarmChannelEditorState? draftState);

            if (!AlarmChannelStatesEqual(committedState, draftState))
            {
                modified.Add(channel);
            }
        }

        return modified;
    }

    private static bool AlarmChannelStatesEqual(AlarmChannelEditorState? left, AlarmChannelEditorState? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.EnableAlarmL1 != right.EnableAlarmL1 ||
            left.EnableDiffAlarm != right.EnableDiffAlarm ||
            left.ZoneCount != right.ZoneCount ||
            left.ZoneLength != right.ZoneLength ||
            Math.Abs(left.TempCorrection - right.TempCorrection) > 0.0001f ||
            left.ZoneRows.Count != right.ZoneRows.Count)
        {
            return false;
        }

        for (int i = 0; i < left.ZoneRows.Count; i++)
        {
            ZoneParameterItem a = left.ZoneRows[i];
            ZoneParameterItem b = right.ZoneRows[i];
            if (a.ZoneNo != b.ZoneNo ||
                !string.Equals(a.Description, b.Description, StringComparison.Ordinal) ||
                a.StartPos != b.StartPos ||
                a.EndPos != b.EndPos ||
                Math.Abs(a.AlarmLevel1 - b.AlarmLevel1) > 0.0001 ||
                Math.Abs(a.DiffTempAlarm - b.DiffTempAlarm) > 0.0001)
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatAlarmChannelList(IEnumerable<int> channels)
    {
        return string.Join("、", channels
            .Distinct()
            .OrderBy(x => x)
            .Select(FormatChannelLabel));
    }

    private void PersistCurrentZoneAlarmEditorState(int? channelIndexOverride = null)
    {
        if (_isRestoringUiState || _isSwitchingZoneChannel)
        {
            return;
        }

        int channelIndex = channelIndexOverride ?? (_activeZoneEditorChannel >= 0 ? _activeZoneEditorChannel : GetSelectedZoneChannelIndex());
        NormalizeZoneThresholds(_zoneParameterItems);

        var state = new AlarmChannelEditorState
        {
            EnableAlarmL1 = EnableAlarmL1CheckBox.IsChecked == true,
            EnableDiffAlarm = EnableDiffAlarmCheckBox.IsChecked == true,
            ZoneCount = Math.Clamp(ParseInt(ZoneCountTextBox.Text, 0), 0, 200),
            ZoneLength = Math.Clamp(ParseInt(ZoneLengthTextBox.Text, 0), 0, 100000),
            TempCorrection = ParseFloat(TempCorrectionTextBox.Text, 0f),
            ZoneRows = CloneZoneParameterItems(_zoneParameterItems)
        };

        GetAlarmEditorStateStore()[channelIndex] = state;
    }

    private void LoadZoneAlarmEditorStateForSelectedChannel()
    {
        int channelIndex = GetSelectedZoneChannelIndex();
        AlarmChannelEditorState state = GetOrCreateAlarmChannelState(channelIndex, out bool created);

        _isSwitchingZoneChannel = true;
        try
        {
            EnableAlarmL1CheckBox.IsChecked = state.EnableAlarmL1;
            EnableDiffAlarmCheckBox.IsChecked = state.EnableDiffAlarm;
            ZoneCountTextBox.Text = state.ZoneCount > 0 ? state.ZoneCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
            ZoneLengthTextBox.Text = state.ZoneLength > 0 ? state.ZoneLength.ToString(CultureInfo.InvariantCulture) : string.Empty;
            TempCorrectionTextBox.Text = Math.Abs(state.TempCorrection) > 0.0001f
                ? state.TempCorrection.ToString("0.0###", CultureInfo.InvariantCulture)
                : string.Empty;

            _zoneParameterItems.Clear();
            foreach (ZoneParameterItem row in state.ZoneRows)
            {
                _zoneParameterItems.Add(CloneZone(row));
            }

            NormalizeZoneThresholds(_zoneParameterItems);
            _activeZoneEditorChannel = channelIndex;
        }
        finally
        {
            _isSwitchingZoneChannel = false;
        }
    }

    private void InitializeAlarmChannelStatesFromLegacyState(UiStateSnapshot state)
    {
        _alarmChannelStatesByChannel.Clear();

        if (state.AlarmSettingsByChannel is not null && state.AlarmSettingsByChannel.Count > 0)
        {
            foreach ((int channel, AlarmChannelStateSnapshot? channelState) in state.AlarmSettingsByChannel)
            {
                if (channelState is null)
                {
                    continue;
                }

                _alarmChannelStatesByChannel[Math.Clamp(channel, 0, MaxMonitorChannels - 1)] = new AlarmChannelEditorState
                {
                    EnableAlarmL1 = channelState.EnableAlarmL1,
                    EnableDiffAlarm = channelState.EnableDiffAlarm,
                    ZoneCount = Math.Clamp(channelState.ZoneCount, 0, 200),
                    ZoneLength = Math.Clamp(channelState.ZoneLength, 0, 100000),
                    TempCorrection = channelState.TempCorrection,
                    ZoneRows = channelState.ZoneRows?
                        .Select(CloneZone)
                        .ToList() ?? new List<ZoneParameterItem>()
                };
            }

            return;
        }

        var legacyState = new AlarmChannelEditorState
        {
            EnableAlarmL1 = state.EnableAlarmL1,
            EnableDiffAlarm = state.EnableDiffAlarm,
            ZoneCount = Math.Clamp(ParseInt(state.ZoneCount ?? string.Empty, 0), 0, 200),
            ZoneLength = Math.Clamp(ParseInt(state.ZoneLength ?? string.Empty, 0), 0, 100000),
            TempCorrection = ParseFloat(state.TempCorrection ?? string.Empty, 0f),
            ZoneRows = state.ZoneRows?
                .Select(CloneZone)
                .ToList() ?? new List<ZoneParameterItem>()
        };

        foreach (ChannelOption option in _channelOptions)
        {
            _alarmChannelStatesByChannel[option.ChannelIndex] = CloneAlarmChannelEditorState(legacyState);
        }
    }

    private static AlarmChannelEditorState CloneAlarmChannelEditorState(AlarmChannelEditorState source)
    {
        return new AlarmChannelEditorState
        {
            EnableAlarmL1 = source.EnableAlarmL1,
            EnableDiffAlarm = source.EnableDiffAlarm,
            ZoneCount = source.ZoneCount,
            ZoneLength = source.ZoneLength,
            TempCorrection = source.TempCorrection,
            ZoneRows = source.ZoneRows.Select(CloneZone).ToList()
        };
    }

    private void LoadUiState()
    {
        if (string.IsNullOrWhiteSpace(_uiStatePath) || !File.Exists(_uiStatePath))
        {
            ApplyDefaultUiState();
            return;
        }

        try
        {
            string json = File.ReadAllText(_uiStatePath);
            UiStateSnapshot? state = JsonSerializer.Deserialize<UiStateSnapshot>(json);
            if (state is null)
            {
                ApplyDefaultUiState();
                return;
            }

            _isRestoringUiState = true;
            _coefficientFilePathsByChannel.Clear();
            _loadedCoefficientProfilesByChannel.Clear();
            _loadedCoefficientProfile = null;
            _activeCoefficientChannel = -1;
            _selectedMonitorChannel = 0;
            _monitorAllChannels = false;

            ChannelTextBox.Text = state.Channel ?? string.Empty;
            StartWlTextBox.Text = state.StartWavelength ?? string.Empty;
            StopWlTextBox.Text = state.StopWavelength ?? string.Empty;
            FiberLengthTextBox.Text = state.FiberLength ?? string.Empty;
            ProfileStepTextBox.Text = state.ProfileStep ?? string.Empty;
            TargetPointsTextBox.Text = state.TargetPoints ?? string.Empty;
            DelayTextBox.Text = state.Delay ?? string.Empty;
            PulseWidthTextBox.Text = state.PulseWidth ?? string.Empty;
            OpticSwitchEnabledTextBox.Text = state.OpticSwitchEnabled ?? string.Empty;
            EdfaCurrentTextBox.Text = state.EdfaCurrent ?? string.Empty;
            EdfaPaCurrentTextBox.Text = state.EdfaPaCurrent ?? string.Empty;
            CalibrationEdfaCurrentTextBox.Text = state.EdfaCurrent ?? string.Empty;
            CalibrationEdfaPaCurrentTextBox.Text = state.EdfaPaCurrent ?? string.Empty;
            FiberDensityTextBox.Text = state.FiberDensity ?? "0";
            WavelengthAverageCountTextBox.Text = state.WavelengthAverageCount ?? "1";
            MultiWaveReverseTextBox.Text = state.MultiWaveReverse
                ?? ((state.CenterWavelengths?.Contains(',') == true) ? "1" : "0");
            SpeedModeTextBox.Text = state.SpeedMode ?? string.Empty;
            LaserTypeTextBox.Text = state.LaserType ?? string.Empty;
            AlgorithmTypeTextBox.Text = state.AlgorithmType ?? string.Empty;
            WavelengthPrecisionModeTextBox.Text = state.WavelengthPrecisionMode ?? string.Empty;
            CenterWavelengthTextBox.Text = state.CenterWavelengths ?? string.Empty;
            CoefficientFilePathTextBox.Text = state.CoefficientFilePath ?? string.Empty;
            ExternalCommPortTextBox.Text = state.ExternalCommPort ?? ExternalCommPortTextBox.Text;
            StorageIntervalTextBox.Text = state.StorageInterval ?? StorageIntervalTextBox.Text;
            DatabaseTableNameTextBox.Text = state.DatabaseTableName ?? DatabaseTableNameTextBox.Text;
            LocalStorageCheckBox.IsChecked = state.LocalStorageEnabled;
            RestoreParameterChannelSettings(state);
            OpticSwitchEnabledCheckBox.IsChecked = OpticSwitchEnabledTextBox.Text != "0";
            MultiWaveReverseCheckBox.IsChecked = MultiWaveReverseTextBox.Text != "0";

            if (state.CoefficientFilePathsByChannel is not null)
            {
                foreach ((int channel, string? path) in state.CoefficientFilePathsByChannel)
                {
                    if (channel < 0 || string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    int resolvedChannel = ResolveCoefficientFileChannel(path, channel);
                    _coefficientFilePathsByChannel[resolvedChannel] = path;
                }
            }

            if (_coefficientFilePathsByChannel.Count == 0 &&
                !string.IsNullOrWhiteSpace(state.CoefficientFilePath))
            {
                int fallbackChannel = 0;
                _coefficientFilePathsByChannel[ResolveCoefficientFileChannel(state.CoefficientFilePath, fallbackChannel)] = state.CoefficientFilePath;
            }

            int restoredMonitorChannel = state.SelectedMonitorChannel ?? GetDefaultMonitorChannelFromState(state);
            _monitorAllChannels = restoredMonitorChannel < 0;
            _selectedMonitorChannel = Math.Clamp(
                _monitorAllChannels ? GetDefaultMonitorChannelFromState(state) : restoredMonitorChannel,
                0,
                MaxMonitorChannels - 1);

            EnableAlarmL1CheckBox.IsChecked = state.EnableAlarmL1;
            EnableDiffAlarmCheckBox.IsChecked = state.EnableDiffAlarm;

            TempCorrectionTextBox.Text = state.TempCorrection ?? string.Empty;
            TemperatureAxisMinTextBox.Text = state.TemperatureAxisMin ?? string.Empty;
            TemperatureAxisMaxTextBox.Text = state.TemperatureAxisMax ?? string.Empty;
            ZoneCountTextBox.Text = state.ZoneCount ?? string.Empty;
            ZoneLengthTextBox.Text = state.ZoneLength ?? string.Empty;

            InitializeAlarmChannelStatesFromLegacyState(state);
            SetZoneChannelByText(state.ZoneChannel);
            EnsureZoneChannelSelection();
            LoadZoneAlarmEditorStateForSelectedChannel();
            MigrateLegacyZoneThresholdDefaults();
            SyncAppliedAlarmStateFromUi();

            SyncChannelSelections(_selectedMonitorChannel);
            SyncSelectedChannelParameterInputs();
            EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        }
        catch
        {
            ApplyDefaultUiState();
        }
        finally
        {
            _isRestoringUiState = false;
        }
    }

    private void ApplyDefaultUiState()
    {
        HardwareConfig defaults = new();

        _isRestoringUiState = true;
        try
        {
            _coefficientFilePathsByChannel.Clear();
            _loadedCoefficientProfilesByChannel.Clear();
            _loadedCoefficientProfile = null;
            _activeCoefficientChannel = -1;
            _selectedMonitorChannel = 0;
            _monitorAllChannels = false;
            _alarmChannelStatesByChannel.Clear();
            _alarmChannelDraftStatesByChannel.Clear();
            _zoneParameterItems.Clear();
            InitializeParameterChannelSettings();

            ChannelTextBox.Text = DisplayChannelBase.ToString(CultureInfo.InvariantCulture);
            StartWlTextBox.Text = defaults.StartWavelengthNm.ToString(CultureInfo.InvariantCulture);
            StopWlTextBox.Text = defaults.StopWavelengthNm.ToString(CultureInfo.InvariantCulture);
            FiberLengthTextBox.Text = defaults.FiberLengthM.ToString(CultureInfo.InvariantCulture);
            ProfileStepTextBox.Text = DefaultProfileStepMeters.ToString(CultureInfo.InvariantCulture);
            TargetPointsTextBox.Text = CalcProfilePointsByStep(defaults.FiberLengthM, DefaultProfileStepMeters).ToString(CultureInfo.InvariantCulture);
            DelayTextBox.Text = defaults.DelayNs.ToString("0.###", CultureInfo.InvariantCulture);
            PulseWidthTextBox.Text = defaults.PulseWidth.ToString(CultureInfo.InvariantCulture);
            OpticSwitchEnabledTextBox.Text = defaults.OpticSwitchEnabled ? "1" : "0";
            EdfaCurrentTextBox.Text = defaults.EdfaCurrentMa.ToString(CultureInfo.InvariantCulture);
            EdfaPaCurrentTextBox.Text = defaults.EdfaPaCurrentMa.ToString(CultureInfo.InvariantCulture);
            CalibrationEdfaCurrentTextBox.Text = defaults.CalibrationEdfaCurrentMa.ToString(CultureInfo.InvariantCulture);
            CalibrationEdfaPaCurrentTextBox.Text = defaults.CalibrationEdfaPaCurrentMa.ToString(CultureInfo.InvariantCulture);
            FiberDensityTextBox.Text = defaults.FiberDensityMode.ToString(CultureInfo.InvariantCulture);
            WavelengthAverageCountTextBox.Text = defaults.WavelengthAverageCount.ToString(CultureInfo.InvariantCulture);
            MultiWaveReverseTextBox.Text = defaults.MultiWaveReverse ? "1" : "0";
            SpeedModeTextBox.Text = defaults.SpeedMode.ToString(CultureInfo.InvariantCulture);
            LaserTypeTextBox.Text = defaults.LaserType.ToString(CultureInfo.InvariantCulture);
            AlgorithmTypeTextBox.Text = defaults.AlgorithmType.ToString(CultureInfo.InvariantCulture);
            WavelengthPrecisionModeTextBox.Text = defaults.WavelengthPrecisionMode.ToString(CultureInfo.InvariantCulture);
            CenterWavelengthTextBox.Text = string.Empty;
            CoefficientFilePathTextBox.Text = string.Empty;
            TempCorrectionTextBox.Text = string.Empty;
            TemperatureAxisMinTextBox.Text = string.Empty;
            TemperatureAxisMaxTextBox.Text = string.Empty;
            ZoneCountTextBox.Text = string.Empty;
            ZoneLengthTextBox.Text = string.Empty;
            EnableAlarmL1CheckBox.IsChecked = false;
            EnableDiffAlarmCheckBox.IsChecked = false;
            ZoneChannelComboBox.SelectedIndex = -1;
            LocalStorageCheckBox.IsChecked = false;

            OpticSwitchEnabledCheckBox.IsChecked = defaults.OpticSwitchEnabled;
            MultiWaveReverseCheckBox.IsChecked = defaults.MultiWaveReverse;
            SyncAcquisitionParameterSelectorsFromTextValues();
            SyncChannelSelections(_selectedMonitorChannel);
            EnsureZoneChannelSelection();
            EnsureAlarmChannelStatesInitialized();
            LoadZoneAlarmEditorStateForSelectedChannel();
        }
        finally
        {
            _isRestoringUiState = false;
        }
    }

    private void SaveUiState()
    {
        if (string.IsNullOrWhiteSpace(_uiStatePath))
        {
            return;
        }

        PersistVisibleCoefficientPathForActiveChannel();
        PersistCurrentZoneAlarmEditorState();
        SaveCurrentDeviceDefinitionFromUi();

        try
        {
            string? dir = IoPath.GetDirectoryName(_uiStatePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var state = new UiStateSnapshot
            {
                Ip = _currentDevice?.Ip ?? string.Empty,
                Channel = ChannelTextBox.Text,
                StartWavelength = StartWlTextBox.Text,
                StopWavelength = StopWlTextBox.Text,
                FiberLength = FiberLengthTextBox.Text,
                ProfileStep = ProfileStepTextBox.Text,
                TargetPoints = TargetPointsTextBox.Text,
                Delay = DelayTextBox.Text,
                PulseWidth = PulseWidthTextBox.Text,
                OpticSwitchEnabled = OpticSwitchEnabledTextBox.Text,
                EdfaCurrent = EdfaCurrentTextBox.Text,
                EdfaPaCurrent = EdfaPaCurrentTextBox.Text,
                CalibrationEdfaCurrent = EdfaCurrentTextBox.Text,
                CalibrationEdfaPaCurrent = EdfaPaCurrentTextBox.Text,
                FiberDensity = FiberDensityTextBox.Text,
                WavelengthAverageCount = WavelengthAverageCountTextBox.Text,
                MultiWaveReverse = MultiWaveReverseTextBox.Text,
                AutoRun = "0",
                SpeedMode = SpeedModeTextBox.Text,
                LaserType = LaserTypeTextBox.Text,
                AlgorithmType = AlgorithmTypeTextBox.Text,
                WavelengthPrecisionMode = WavelengthPrecisionModeTextBox.Text,
                CenterWavelengths = CenterWavelengthTextBox.Text,
                CoefficientFilePath = CoefficientFilePathTextBox.Text,
                ExternalCommPort = ExternalCommPortTextBox.Text,
                StorageInterval = StorageIntervalTextBox.Text,
                DatabaseTableName = DatabaseTableNameTextBox.Text,
                LocalStorageEnabled = LocalStorageCheckBox.IsChecked == true,
                SelectedMonitorChannel = _monitorAllChannels ? -1 : _selectedMonitorChannel,
                CenterWavelengthsByChannel = _parameterChannelSettings
                    .Where(x => !string.IsNullOrWhiteSpace(x.CenterWavelengthText))
                    .ToDictionary(
                        x => x.ChannelIndex,
                        x => NormalizeCenterWavelengthText(x.CenterWavelengthText)),
                ChannelEnabledByChannel = _parameterChannelSettings
                    .ToDictionary(x => x.ChannelIndex, x => x.IsEnabled),
                CoefficientFilePathsByChannel = _coefficientFilePathsByChannel
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                    .ToDictionary(x => x.Key, x => x.Value),

                EnableAlarmL1 = EnableAlarmL1CheckBox.IsChecked == true,
                EnableDiffAlarm = EnableDiffAlarmCheckBox.IsChecked == true,
                ZoneChannel = GetSelectedZoneChannelText(),
                TempCorrection = TempCorrectionTextBox.Text,
                TemperatureAxisMin = TemperatureAxisMinTextBox.Text,
                TemperatureAxisMax = TemperatureAxisMaxTextBox.Text,
                ZoneCount = ZoneCountTextBox.Text,
                ZoneLength = ZoneLengthTextBox.Text,
                AlarmSettingsByChannel = _alarmChannelStatesByChannel
                    .ToDictionary(
                        x => x.Key,
                        x => new AlarmChannelStateSnapshot
                        {
                            EnableAlarmL1 = x.Value.EnableAlarmL1,
                            EnableDiffAlarm = x.Value.EnableDiffAlarm,
                            ZoneCount = x.Value.ZoneCount,
                            ZoneLength = x.Value.ZoneLength,
                            TempCorrection = x.Value.TempCorrection,
                            ZoneRows = x.Value.ZoneRows.Select(CloneZone).ToList()
                        }),
                ZoneRows = _zoneParameterItems
                    .Select(z => new ZoneParameterItem
                    {
                        ZoneNo = z.ZoneNo,
                        Description = z.Description,
                        StartPos = z.StartPos,
                        EndPos = z.EndPos,
                        AlarmLevel1 = z.AlarmLevel1,
                        DiffTempAlarm = z.DiffTempAlarm
                    })
                    .ToList()
            };

            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_uiStatePath, json);
        }
        catch
        {
        }
    }

    private static string NormalizeCenterWavelengthText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Regex.Replace(text.Trim(), "\\s+", " ");
    }

    private void SaveCurrentDeviceDefinitionFromUi()
    {
        if (_currentDevice is null || _deviceRegistry is null)
        {
            return;
        }

        _currentDevice.LastModifiedUtc = DateTime.UtcNow;
        _deviceRegistry.Save(_devices);
    }

    private void SetZoneChannelByText(string? channelText)
    {
        ZoneChannelComboBox.SelectedIndex = -1;
        if (string.IsNullOrWhiteSpace(channelText))
        {
            return;
        }

        foreach (var item in ZoneChannelComboBox.Items)
        {
            if (item is ChannelOption option &&
                option.DisplayText is string text &&
                string.Equals(text, channelText, StringComparison.Ordinal))
            {
                ZoneChannelComboBox.SelectedItem = option;
                return;
            }
        }
    }

    private void ZoneChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringUiState || _isSwitchingZoneChannel)
        {
            return;
        }

        if (_activeZoneEditorChannel >= 0)
        {
            PersistCurrentZoneAlarmEditorState(_activeZoneEditorChannel);
        }
        LoadZoneAlarmEditorStateForSelectedChannel();
        if (!_isAlarmDialogOpen)
        {
            SyncAppliedAlarmStateFromUi();
            ApplyAlarmSettingsToService();
            SaveUiState();
        }
    }

    private void DrawShapeSensingViews(SnapshotModel snapshot)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (_lastShapeSensingTimestampMs == snapshot.TimestampMs ||
            (_latestShapeResult is not null && nowUtc - _lastShapeSensingRefreshUtc < ShapeSensingRefreshInterval))
        {
            return;
        }

        _lastShapeSensingRefreshUtc = nowUtc;
        _lastShapeSensingTimestampMs = snapshot.TimestampMs;

        ShapeSensingProfile? profile = BuildShapeSensingProfile(snapshot.Channel);
        snapshot = AlignSnapshotSensorDataToLoadedProfile(snapshot);
        snapshot = ApplyShapeWavelengthMedianFilter(snapshot);
        ShapeReconstructionSettings settings = BuildShapeReconstructionSettings(snapshot.SensorWavelengthsNm.Length);
        _shapeReferenceTopByChannel.TryGetValue(snapshot.Channel, out float[]? referenceTop);
        _shapeReferenceBottomByChannel.TryGetValue(snapshot.Channel, out float[]? referenceBottom);

        ShapeReconstructionResult result = ShapeReconstructionService.Reconstruct2D(
            snapshot,
            profile,
            settings,
            referenceTop,
            referenceBottom);

        _latestShapeResult = result;
        if (!result.IsValid)
        {
            _singleSensorStrainChartData = null;
            _strainArrayChartData = null;
            _shapeReconstructionChartData = null;
            SingleSensorStrainCanvas.Children.Clear();
            StrainArrayCanvas.Children.Clear();
            ShapeReconstructionCanvas.Children.Clear();
            ShapeStatusTextBlock.Text = result.StatusText;
            return;
        }

        string modeText = result.Mode == ShapeSensingMode.DualFiber ? "双光纤差分" : "单光纤相对基准";
        string countUnit = result.Mode == ShapeSensingMode.DualFiber ? "组光栅" : "个光栅";
        string reliabilityText = result.Mode == ShapeSensingMode.DualFiber
            ? "，上下差分重构"
            : "，趋势估计，非真实二维形状";
        ShapeStatusTextBlock.Text = $"{FormatChannelLabel(result.Channel)} {modeText}形状重构正常：{result.PairCount} {countUnit}，最大挠度 {result.MaxDeflectionM * 100.0f:F3} cm{reliabilityText}";
        float[] axialStrain = BuildAxialStrainMicro(result);
        _latestAxialStrainByChannel[snapshot.Channel] = axialStrain;
        RefreshSensorOptions(snapshot, preserveScroll: true, ensureSelectedRowVisible: false);
        DrawSingleSensorStrainTrend(snapshot, result, axialStrain);
        DrawStrainArray(snapshot, result, axialStrain);
        DrawShapeReconstruction(result);
    }

    private ShapeReconstructionSettings BuildShapeReconstructionSettings(int wavelengthCount)
    {
        int defaultEnd = Math.Max(0, wavelengthCount - 1);
        int defaultOffset = Math.Max(1, wavelengthCount / 2);
        return new ShapeReconstructionSettings
        {
            StartIndex = 0,
            EndIndex = defaultEnd,
            PairOffset = defaultOffset,
            Mode = GetSelectedShapeSensingMode(),
            GratingDistanceM = 0.003f,
            NeutralAxisDistanceM = 0.003f,
            FineStepM = 0.005f,
            SmoothWindow = 7,
            AutoScale = true,
            XAxisMaxM = 20f,
            YAxisMaxM = 0.1f
        };
    }

    private static float[] BuildAxialStrainMicro(ShapeReconstructionResult result)
    {
        int count = Math.Min(result.StrainTopMicro.Length, result.StrainBottomMicro.Length);
        float[] axial = new float[count];
        for (int i = 0; i < count; i++)
        {
            float top = result.StrainTopMicro[i];
            float bottom = result.StrainBottomMicro[i];
            axial[i] = float.IsFinite(top) && float.IsFinite(bottom)
                ? 0.5f * (top + bottom)
                : float.NaN;
        }

        return axial;
    }

    private void DrawSingleSensorStrainTrend(SnapshotModel snapshot, ShapeReconstructionResult result, float[] axialStrain)
    {
        int pairIndex = ResolveActiveShapePairIndex(snapshot, result.PairCount);
        float currentValue = pairIndex >= 0 && pairIndex < axialStrain.Length ? axialStrain[pairIndex] : float.NaN;
        AppendTrendValue(_singleSensorStrainTrendByKey, (snapshot.Channel, pairIndex), currentValue, requirePositive: false);
        IReadOnlyList<float> history = GetTrendValuesForDisplay(
            _singleSensorStrainTrendByKey,
            (snapshot.Channel, pairIndex),
            currentValue,
            requirePositive: false);

        if (history.Count == 0)
        {
            _singleSensorStrainChartData = null;
            SingleSensorStrainCanvas.Children.Clear();
            return;
        }

        _singleSensorStrainChartData = BuildSingleSensorTrendChart(
            history,
            "次数",
            "应变 (με)",
            BrushFromHex("#F8C14A"),
            "F0",
            "F2",
            -StrainDisplayHalfRangeMicro,
            StrainDisplayHalfRangeMicro,
            showZeroLine: true);

        DrawChart(SingleSensorStrainCanvas, _singleSensorStrainChartData, _singleSensorStrainViewport);
        DrawShapeReconstructionZoomChart();
    }

    private int ResolveActiveShapePairIndex(SnapshotModel snapshot, int pairCount)
    {
        if (pairCount <= 0)
        {
            return -1;
        }

        int sensorIndex = ResolveActiveSensorIndex(snapshot);
        if (sensorIndex < 0)
        {
            return 0;
        }

        return Math.Clamp(sensorIndex >= pairCount ? sensorIndex - pairCount : sensorIndex, 0, pairCount - 1);
    }

    private void DrawStrainArray(SnapshotModel snapshot, ShapeReconstructionResult result, float[] axialStrain)
    {
        float[] positions = ResolveShapeArrayDisplayPositions(snapshot, result.PairCount, result.Mode)
            .Take(axialStrain.Length)
            .ToArray();
        if (positions.Length == 0)
        {
            positions = Enumerable.Range(0, axialStrain.Length).Select(i => (float)i).ToArray();
        }

        float[] validPositions = positions.Where(float.IsFinite).ToArray();
        float minDistance = validPositions.Length > 0 ? validPositions[0] : 0f;
        float maxDistance = validPositions.Length > 0 ? validPositions[^1] : 1f;
        if (maxDistance <= minDistance)
        {
            maxDistance = validPositions.DefaultIfEmpty(minDistance + 1f).Max();
        }

        _strainArrayChartData = new ChartSeriesData(
            positions,
            axialStrain,
            "距离 (m)",
            "轴向应变 (με)",
            BrushFromHex("#F8C14A"),
            showMarkers: true,
            markerDiameter: 5,
            markerBrush: BrushFromHex("#F8C14A"),
            enablePointHover: true,
            defaultMinX: minDistance,
            defaultMaxX: Math.Max(minDistance + 1f, maxDistance),
            defaultMinY: -StrainDisplayHalfRangeMicro,
            defaultMaxY: StrainDisplayHalfRangeMicro,
            xTickFormat: "F2",
            yTickFormat: "F2",
            xTickCount: 6,
            yTickCount: 5,
            showZeroLine: true);

        DrawChart(StrainArrayCanvas, _strainArrayChartData, _strainArrayViewport);
        DrawShapeReconstructionZoomChart();
    }

    private float[] ResolveShapeArrayDisplayPositions(SnapshotModel snapshot, int pointCount, ShapeSensingMode mode)
    {
        if (pointCount <= 0)
        {
            return Array.Empty<float>();
        }

        BuildDisplaySensorSeries(
            snapshot,
            out _,
            out float[] displayPositions,
            out _,
            out _);

        if (displayPositions.Length > 0)
        {
            return mode == ShapeSensingMode.DualFiber
                ? BuildPairDisplayPositionsFromCalibrationRange(displayPositions, pointCount)
                : displayPositions.Take(pointCount).ToArray();
        }

        return resultFallback();

        float[] resultFallback()
        {
            if (snapshot.SensorPositionsM.Length > 0)
            {
                return snapshot.SensorPositionsM.Take(pointCount).ToArray();
            }

            return Enumerable.Range(0, pointCount).Select(i => (float)i).ToArray();
        }
    }

    private static float[] BuildPairDisplayPositionsFromCalibrationRange(float[] displayPositions, int pairCount)
    {
        if (pairCount <= 0)
        {
            return Array.Empty<float>();
        }

        float[] validPositions = displayPositions
            .Where(float.IsFinite)
            .OrderBy(x => x)
            .ToArray();
        if (validPositions.Length == 0)
        {
            return Array.Empty<float>();
        }

        if (validPositions.Length >= pairCount * 2 && pairCount > 1)
        {
            float min = validPositions[0];
            float max = validPositions[^1];
            float firstHalfMax = validPositions.Take(pairCount).DefaultIfEmpty(min).Max();
            if (max > min && firstHalfMax < max * 0.85f)
            {
                float step = (max - min) / (pairCount - 1);
                return Enumerable.Range(0, pairCount)
                    .Select(i => min + step * i)
                    .ToArray();
            }
        }

        return validPositions.Take(pairCount).ToArray();
    }

    private float[] ResolveSensorInfoStrainValues(
        int channel,
        ref int[] rawSensorIndexes,
        ref float[] positions,
        ref float[] wavelengths,
        ref float[] temperatures)
    {
        if (!_latestAxialStrainByChannel.TryGetValue(channel, out float[]? strains) || strains.Length == 0)
        {
            return Array.Empty<float>();
        }

        int count = Math.Min(strains.Length, positions.Length);
        if (count <= 0)
        {
            return Array.Empty<float>();
        }

        rawSensorIndexes = rawSensorIndexes.Take(count).ToArray();
        positions = positions.Take(count).ToArray();
        wavelengths = wavelengths.Take(count).ToArray();
        temperatures = temperatures.Take(count).ToArray();
        return strains.Take(count).ToArray();
    }

    private void DrawShapeReconstruction(ShapeReconstructionResult result)
    {
        float xOffset = result.ArcPositionsM.FirstOrDefault(float.IsFinite);
        float[] displayX = result.ShapeX
            .Select(x => float.IsFinite(x) ? x + xOffset : x)
            .ToArray();
        float minX = displayX.Where(float.IsFinite).DefaultIfEmpty(xOffset).Min();
        float maxX = displayX.Where(float.IsFinite).DefaultIfEmpty(xOffset + 1f).Max();
        float maxAbsY = result.ShapeY.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0.01f).Max();
        float shapeHalfRange = result.Mode == ShapeSensingMode.SingleFiber
            ? SingleFiberShapeDisplayHalfRangeM
            : Math.Max(0.01f, maxAbsY * 1.25f);
        _shapeReconstructionChartData = new ChartSeriesData(
            displayX,
            result.ShapeY,
            "距离 (m)",
            "挠度 (m)",
            BrushFromHex("#7CF3D0"),
            showMarkers: false,
            enablePointHover: true,
            defaultMinX: minX,
            defaultMaxX: Math.Max(minX + 1f, maxX),
            defaultMinY: -shapeHalfRange,
            defaultMaxY: shapeHalfRange,
            xTickFormat: "F2",
            yTickFormat: "F4",
            xTickCount: 6,
            yTickCount: 5,
            showZeroLine: true);

        DrawChart(ShapeReconstructionCanvas, _shapeReconstructionChartData, _shapeReconstructionViewport);
        DrawShapeReconstructionZoomChart();
    }
    private void DrawSensorSpectrum(float[] wavelengthsNm, float[] spectrumValues)
    {
        float minWavelength = _config.StartWavelengthNm > 0 ? _config.StartWavelengthNm : 1528f;
        float maxWavelength = _config.StopWavelengthNm > 0 ? _config.StopWavelengthNm : 1568f;
        float? minSpectrumY = null;
        float? maxSpectrumY = null;
        float[] finiteValues = spectrumValues.Where(float.IsFinite).ToArray();
        if (finiteValues.Length > 0)
        {
            float dataMin = finiteValues.Min();
            float dataMax = finiteValues.Max();
            float band = Math.Max(0.05f, dataMax - dataMin);
            minSpectrumY = dataMin - band;
            maxSpectrumY = minSpectrumY + band * 5.0f;
        }

        _sensorSpectrumChartData = new ChartSeriesData(
            wavelengthsNm,
            spectrumValues,
            "波长 (nm)",
            "光谱强度",
            BrushFromHex("#8BC7FF"),
            defaultMinX: Math.Min(minWavelength, maxWavelength),
            defaultMaxX: Math.Max(minWavelength, maxWavelength),
            defaultMinY: minSpectrumY,
            defaultMaxY: maxSpectrumY,
            xTickFormat: "F4",
            yTickFormat: "F4",
            xTickCount: 6,
            yTickCount: 5);
        DrawChart(
            SensorSpectrumCanvas,
            _sensorSpectrumChartData,
            _sensorSpectrumViewport);
    }

    private void DrawTemperatureWaveform(float[] positions, float[] samples)
    {
        float[] validPositions = positions.Where(float.IsFinite).ToArray();
        float minDistance = validPositions.Length > 0 ? validPositions[0] : 0f;
        float maxDistance = validPositions.Length > 0 ? validPositions[^1] : 1f;
        if (maxDistance <= minDistance)
        {
            maxDistance = validPositions.DefaultIfEmpty(minDistance + 1f).Max();
        }

        _temperatureChartData = new ChartSeriesData(
            positions,
            samples,
            "距离 (m)",
            "温度 (℃)",
            BrushFromHex("#67DCFF"),
            showMarkers: true,
            markerDiameter: 5,
            markerBrush: BrushFromHex("#67DCFF"),
            enablePointHover: true,
            defaultMinX: minDistance,
            defaultMaxX: Math.Max(minDistance + 1f, maxDistance),
            defaultMinY: _temperatureAxisMinOverride,
            defaultMaxY: _temperatureAxisMaxOverride,
            xTickFormat: "F1",
            yTickFormat: "F2",
            xTickCount: 6,
            yTickCount: 5);
        DrawChart(
            WaveformCanvas,
            _temperatureChartData,
            _temperatureViewport);
    }

    private void DrawWavelengthArray(float[] positions, float[] wavelengths)
    {
        float[] validPositions = positions.Where(float.IsFinite).ToArray();
        float minDistance = validPositions.Length > 0 ? validPositions[0] : 0f;
        float maxDistance = validPositions.Length > 0 ? validPositions[^1] : 1f;
        if (maxDistance <= minDistance)
        {
            maxDistance = validPositions.DefaultIfEmpty(minDistance + 1f).Max();
        }

        _spectrumChartData = new ChartSeriesData(
            positions,
            wavelengths,
            "距离 (m)",
            "波长 (nm)",
            BrushFromHex("#8BC7FF"),
            showMarkers: true,
            markerDiameter: 5,
            markerBrush: BrushFromHex("#8BC7FF"),
            markerRenderLimit: 10000,
            enablePointHover: true,
            defaultMinX: minDistance,
            defaultMaxX: Math.Max(minDistance + 1f, maxDistance),
            xTickFormat: "F2",
            yTickFormat: "F4",
            xTickCount: 6,
            yTickCount: 5);
        DrawChart(
            SpectrumCanvas,
            _spectrumChartData,
            _spectrumViewport);
    }

    private void DrawSingleSensorWavelengthTrend(SnapshotModel snapshot, int[] rawSensorIndexes, float[] wavelengths)
    {
        int sensorIndex = ResolveActiveSensorIndex(snapshot);
        if (sensorIndex < 0)
        {
            _singleSensorWavelengthChartData = null;
            SingleSensorWavelengthCanvas.Children.Clear();
            return;
        }

        float currentValue = TryGetDisplaySensorValue(sensorIndex, rawSensorIndexes, wavelengths, out float wavelength)
            ? wavelength
            : float.NaN;
        IReadOnlyList<float> history = GetTrendValuesForDisplay(
            _singleSensorWavelengthTrendByKey,
            (snapshot.Channel, sensorIndex),
            currentValue,
            requirePositive: true);

        if (history.Count == 0)
        {
            _singleSensorWavelengthChartData = null;
            SingleSensorWavelengthCanvas.Children.Clear();
            return;
        }

        _singleSensorWavelengthChartData = BuildSingleSensorTrendChart(
            history,
            "次数",
            "波长 (nm)",
            BrushFromHex("#8BC7FF"),
            "F0",
            "F4");

        DrawChart(
            SingleSensorWavelengthCanvas,
            _singleSensorWavelengthChartData,
            _singleSensorWavelengthViewport);
    }

    private void DrawSingleSensorTemperatureTrend(SnapshotModel snapshot, int[] rawSensorIndexes, float[] temperatures)
    {
        int sensorIndex = ResolveActiveSensorIndex(snapshot);
        if (sensorIndex < 0)
        {
            _singleSensorTemperatureChartData = null;
            SingleSensorTemperatureCanvas.Children.Clear();
            return;
        }

        float currentValue = TryGetDisplaySensorValue(sensorIndex, rawSensorIndexes, temperatures, out float temperature)
            ? temperature
            : float.NaN;
        IReadOnlyList<float> history = GetTrendValuesForDisplay(
            _singleSensorTemperatureTrendByKey,
            (snapshot.Channel, sensorIndex),
            currentValue,
            requirePositive: false);

        if (history.Count == 0)
        {
            _singleSensorTemperatureChartData = null;
            SingleSensorTemperatureCanvas.Children.Clear();
            return;
        }

        _singleSensorTemperatureChartData = BuildSingleSensorTrendChart(
            history,
            "次数",
            "温度 (℃)",
            BrushFromHex("#67DCFF"),
            "F0",
            "F2");

        DrawChart(
            SingleSensorTemperatureCanvas,
            _singleSensorTemperatureChartData,
            _singleSensorTemperatureViewport);
    }

    private ChartSeriesData BuildSingleSensorTrendChart(
        IReadOnlyList<float> values,
        string xLabel,
        string yLabel,
        Brush lineBrush,
        string xTickFormat,
        string yTickFormat,
        float? defaultMinY = null,
        float? defaultMaxY = null,
        bool showZeroLine = false)
    {
        float[] xAxis = Enumerable.Range(1, values.Count).Select(i => (float)i).ToArray();
        float maxX = Math.Max(2, values.Count);
        return new ChartSeriesData(
            xAxis,
            values,
            xLabel,
            yLabel,
            lineBrush,
            showMarkers: true,
            markerDiameter: 5,
            markerBrush: lineBrush,
            enablePointHover: true,
            defaultMinX: 1f,
            defaultMaxX: maxX,
            defaultMinY: defaultMinY,
            defaultMaxY: defaultMaxY,
            xTickFormat: xTickFormat,
            yTickFormat: yTickFormat,
            xTickCount: Math.Max(1, values.Count - 1),
            yTickCount: 5,
            showZeroLine: showZeroLine);
    }

    private void AppendSingleSensorTrend(SnapshotModel snapshot)
    {
        if (snapshot.SensorTemperaturesC.Length > 0)
        {
            for (int sensorIndex = 0; sensorIndex < snapshot.SensorTemperaturesC.Length; sensorIndex++)
            {
                AppendTrendValue(
                    _singleSensorTemperatureTrendByKey,
                    (snapshot.Channel, sensorIndex),
                    snapshot.SensorTemperaturesC[sensorIndex],
                    requirePositive: false);
            }
        }
        else
        {
            int sensorIndex = snapshot.SpectrumSensorIndex;
            float[] sensorTemps = snapshot.TemperaturesC;
            if (sensorIndex >= 0 && sensorIndex < sensorTemps.Length)
            {
                AppendTrendValue(_singleSensorTemperatureTrendByKey, (snapshot.Channel, sensorIndex), sensorTemps[sensorIndex], requirePositive: false);
            }
        }

        for (int sensorIndex = 0; sensorIndex < snapshot.SensorWavelengthsNm.Length; sensorIndex++)
        {
            AppendTrendValue(
                _singleSensorWavelengthTrendByKey,
                (snapshot.Channel, sensorIndex),
                snapshot.SensorWavelengthsNm[sensorIndex],
                requirePositive: true);
        }
    }

    private static void AppendTrendValue(
        Dictionary<(int Channel, int SensorIndex), List<float>> target,
        (int Channel, int SensorIndex) key,
        float value,
        bool requirePositive)
    {
        if (!float.IsFinite(value) || (requirePositive && value <= 0))
        {
            return;
        }

        if (!target.TryGetValue(key, out List<float>? values))
        {
            values = new List<float>(SingleSensorTrendPointLimit);
            target[key] = values;
        }

        values.Add(value);
        if (values.Count > SingleSensorTrendPointLimit)
        {
            values.RemoveAt(0);
        }
    }

    private static IReadOnlyList<float> GetTrendValuesForDisplay(
        Dictionary<(int Channel, int SensorIndex), List<float>> source,
        (int Channel, int SensorIndex) key,
        float currentValue,
        bool requirePositive)
    {
        if (source.TryGetValue(key, out List<float>? values) && values.Count > 0)
        {
            return values;
        }

        if (float.IsFinite(currentValue) && (!requirePositive || currentValue > 0))
        {
            return new[] { currentValue };
        }

        return Array.Empty<float>();
    }

    private int ResolveActiveSensorIndex(SnapshotModel snapshot)
    {
        if (SensorInfoGrid.SelectedItem is SensorInfoRow row)
        {
            return row.SensorIndex;
        }

        return snapshot.SpectrumSensorIndex;
    }

    private void DrawChart(Canvas canvas, ChartSeriesData? chartData, ChartViewportState viewport)
    {
        canvas.Children.Clear();

        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 1 || height <= 1 || chartData is null || chartData.XAxis.Count == 0 || chartData.Values.Count == 0)
        {
            return;
        }

        int count = Math.Min(chartData.XAxis.Count, chartData.Values.Count);
        var validPoints = new List<(float X, float Y)>(count);
        for (int i = 0; i < count; i++)
        {
            float x = chartData.XAxis[i];
            float y = chartData.Values[i];
            if (float.IsFinite(x) && float.IsFinite(y))
            {
                validPoints.Add((x, y));
            }
        }

        if (validPoints.Count == 0)
        {
            return;
        }

        float dataMinX = validPoints.Min(p => p.X);
        float dataMaxX = validPoints.Max(p => p.X);
        float dataMinY = validPoints.Min(p => p.Y);
        float dataMaxY = validPoints.Max(p => p.Y);
        float fullMinX = ResolveRangeMin(chartData.DefaultMinX, dataMinX);
        float fullMaxX = ResolveRangeMax(chartData.DefaultMaxX, dataMaxX, fullMinX);
        float fullMinY = ResolveRangeMin(chartData.DefaultMinY, dataMinY);
        float fullMaxY = ResolveRangeMax(chartData.DefaultMaxY, dataMaxY, fullMinY);

        if (!viewport.TryGetEffectiveRange(fullMinX, fullMaxX, fullMinY, fullMaxY, out float minX, out float maxX, out float minY, out float maxY))
        {
            minX = fullMinX;
            maxX = fullMaxX;
            minY = fullMinY;
            maxY = fullMaxY;
            viewport.Reset();
        }

        if (ReferenceEquals(canvas, WaveformCanvas))
        {
            bool hasVisibleYPoint = validPoints.Any(p =>
                p.X >= minX &&
                p.X <= maxX &&
                p.Y >= minY &&
                p.Y <= maxY);

            if (!hasVisibleYPoint)
            {
                minY = fullMinY;
                maxY = fullMaxY;
                _temperatureViewport.Set(minX, maxX, minY, maxY);
                _temperatureAxisMinOverride = null;
                _temperatureAxisMaxOverride = null;
                if (TemperatureAxisMinTextBox is not null)
                {
                    TemperatureAxisMinTextBox.Text = string.Empty;
                }
                if (TemperatureAxisMaxTextBox is not null)
                {
                    TemperatureAxisMaxTextBox.Text = string.Empty;
                }
            }
        }

        float xRange = Math.Max(0.0001f, maxX - minX);
        float yRange = Math.Max(0.0001f, maxY - minY);

        ChartMargins margins = GetChartMargins(canvas);
        double plotWidth = Math.Max(10, width - margins.Left - margins.Right);
        double plotHeight = Math.Max(10, height - margins.Top - margins.Bottom);
        var plotClip = new RectangleGeometry(new Rect(margins.Left, margins.Top, plotWidth, plotHeight));

        Brush axisBrush = BrushFromHex("#6789B5");
        Brush gridBrush = BrushFromHex("#29496F");
        Brush textBrush = BrushFromHex("#B8D7FF");

        canvas.Children.Add(new RectangleShape {
            Width = plotWidth,
            Height = plotHeight,
            Stroke = axisBrush,
            StrokeThickness = 1,
            Fill = Brushes.Transparent
        });
        Canvas.SetLeft(canvas.Children[^1], margins.Left);
        Canvas.SetTop(canvas.Children[^1], margins.Top);

        int xTicks = GetAdaptiveTickCount(chartData, minX, maxX, plotWidth, 11);
        int yTicks = Math.Max(2, chartData.YTickCount);
        for (int i = 0; i <= xTicks; i++)
        {
            double x = margins.Left + plotWidth * i / xTicks;
            canvas.Children.Add(new LineShape {
                X1 = x,
                X2 = x,
                Y1 = margins.Top,
                Y2 = margins.Top + plotHeight,
                Stroke = gridBrush,
                StrokeThickness = i == 0 || i == xTicks ? 0 : 0.8
            });

            float tickValue = minX + (maxX - minX) * i / xTicks;
            AddCanvasText(
                canvas,
                tickValue.ToString(chartData.XTickFormat, CultureInfo.InvariantCulture),
                x,
                margins.Top + plotHeight + XAxisTickLabelOffset,
                textBrush,
                11,
                anchor: CanvasTextAnchor.TopCenter);
        }

        for (int i = 0; i <= yTicks; i++)
        {
            double y = margins.Top + plotHeight * i / yTicks;
            canvas.Children.Add(new LineShape {
                X1 = margins.Left,
                X2 = margins.Left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = i == 0 || i == yTicks ? 0 : 0.8
            });

            float tickValue = maxY - (maxY - minY) * i / yTicks;
            AddCanvasText(
                canvas,
                tickValue.ToString(chartData.YTickFormat, CultureInfo.InvariantCulture),
                margins.Left - 12,
                y,
                textBrush,
                11,
                anchor: CanvasTextAnchor.RightCenter);
        }

        if (chartData.ShowZeroLine && minY < 0f && maxY > 0f)
        {
            double zeroY = margins.Top + (1.0 - ((0f - minY) / yRange)) * plotHeight;
            canvas.Children.Add(new LineShape {
                X1 = margins.Left,
                X2 = margins.Left + plotWidth,
                Y1 = zeroY,
                Y2 = zeroY,
                Stroke = BrushFromHex("#8FB6E8"),
                StrokeThickness = 1.1,
                StrokeDashArray = new DoubleCollection { 5, 4 },
                Opacity = 0.85
            });
        }

        AddCanvasText(
            canvas,
            chartData.XLabel,
            margins.Left + plotWidth / 2,
            margins.Top + plotHeight + XAxisTitleOffset,
            textBrush,
            12,
            anchor: CanvasTextAnchor.TopCenter);
        AddCanvasText(
            canvas,
            chartData.YLabel,
            8,
            margins.Top + plotHeight / 2,
            textBrush,
            12,
            rotation: -90,
            anchor: CanvasTextAnchor.Center);

        List<(float X, float Y)> visiblePoints = validPoints
            .Where(p => p.X >= minX && p.X <= maxX)
            .ToList();
        if (visiblePoints.Count == 0)
        {
            visiblePoints = validPoints;
        }

        IReadOnlyList<(float X, float Y)> renderPoints = BuildChartRenderPoints(visiblePoints);
        var points = new PointCollection();
        var sampledScreenPoints = new List<Point>(renderPoints.Count);

        for (int i = 0; i < renderPoints.Count; i++)
        {
            double x = margins.Left + ((renderPoints[i].X - minX) / xRange) * plotWidth;
            double normalized = (renderPoints[i].Y - minY) / yRange;
            double y = margins.Top + (1.0 - normalized) * plotHeight;
            Point point = new Point(x, y);
            points.Add(point);
            sampledScreenPoints.Add(point);
        }

        canvas.Children.Add(new PolylineShape {
            Points = points,
            Stroke = chartData.LineBrush,
            StrokeThickness = chartData.ShowMarkers ? 1.2 : 2.0,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true,
            Clip = plotClip
        });

        int markerRenderLimit = chartData.MarkerRenderLimit ?? MaxChartMarkerRenderPoints;
        if (chartData.ShowMarkers && sampledScreenPoints.Count <= markerRenderLimit)
        {
            double markerSize = chartData.MarkerDiameter;
            double coverSize = markerSize + 0.0;
            foreach (Point point in sampledScreenPoints)
            {
                if (point.X < margins.Left ||
                    point.X > margins.Left + plotWidth ||
                    point.Y < margins.Top ||
                    point.Y > margins.Top + plotHeight)
                {
                    continue;
                }

                var cover = new System.Windows.Shapes.Ellipse
                {
                    Width = coverSize,
                    Height = coverSize,
                    Fill = BrushFromHex("#081A35"),
                    StrokeThickness = 0
                };
                canvas.Children.Add(cover);
                Canvas.SetLeft(cover, point.X - coverSize / 2);
                Canvas.SetTop(cover, point.Y - coverSize / 2);
                Canvas.SetZIndex(cover, 10);

                var marker = new System.Windows.Shapes.Ellipse
                {
                    Width = markerSize,
                    Height = markerSize,
                    Fill = chartData.MarkerBrush,
                    StrokeThickness = 0
                };
                canvas.Children.Add(marker);
                Canvas.SetLeft(marker, point.X - markerSize / 2);
                Canvas.SetTop(marker, point.Y - markerSize / 2);
                Canvas.SetZIndex(marker, 11);
            }
        }

        if (viewport.HasCustomRange)
        {
            AddCanvasText(
                canvas,
                "右键重置缩放",
                margins.Left + plotWidth,
                Math.Max(2, margins.Top - 16),
                BrushFromHex("#7FB4F0"),
                11,
                anchor: CanvasTextAnchor.TopRight);
        }

        if (_chartSelectionState is not null && ReferenceEquals(_chartSelectionState.Canvas, canvas))
        {
            if (!canvas.Children.Contains(_chartSelectionState.SelectionRectangle))
            {
                canvas.Children.Add(_chartSelectionState.SelectionRectangle);
            }

            UpdateSelectionRectangle(_chartSelectionState);
            Canvas.SetZIndex(_chartSelectionState.SelectionRectangle, 20);
        }
    }

    private static IReadOnlyList<(float X, float Y)> BuildChartRenderPoints(List<(float X, float Y)> visiblePoints)
    {
        if (visiblePoints.Count <= MaxChartRenderPoints)
        {
            return visiblePoints;
        }

        var renderPoints = new List<(float X, float Y)>(MaxChartRenderPoints);
        double step = (visiblePoints.Count - 1) / (double)(MaxChartRenderPoints - 1);
        int lastIndex = -1;
        for (int i = 0; i < MaxChartRenderPoints; i++)
        {
            int index = (int)Math.Round(i * step);
            index = Math.Clamp(index, 0, visiblePoints.Count - 1);
            if (index == lastIndex)
            {
                continue;
            }

            renderPoints.Add(visiblePoints[index]);
            lastIndex = index;
        }

        return renderPoints;
    }

    private static string FormatFinite(float value, string format)
    {
        return float.IsFinite(value)
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : "--";
    }

    private static float ResolveRangeMin(float? configuredValue, float fallbackValue)
    {
        return configuredValue is float value && float.IsFinite(value)
            ? value
            : fallbackValue;
    }

    private static float ResolveRangeMax(float? configuredValue, float fallbackValue, float minValue)
    {
        float value = configuredValue is float configured && float.IsFinite(configured)
            ? configured
            : fallbackValue;
        return value - minValue < 0.0001f ? minValue + 1f : value;
    }

    private static void AddCanvasText(Canvas canvas, string text, double x, double y, Brush foreground, double fontSize, double rotation = 0, CanvasTextAnchor anchor = CanvasTextAnchor.TopLeft)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = tb.DesiredSize;
        Point origin = GetAnchoredTextOrigin(x, y, size, anchor);
        if (Math.Abs(rotation) > 0.1)
        {
            tb.RenderTransform = new RotateTransform(rotation);
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        canvas.Children.Add(tb);
        Canvas.SetLeft(tb, origin.X);
        Canvas.SetTop(tb, origin.Y);
    }

    private static int GetAdaptiveTickCount(ChartSeriesData chartData, float minX, float maxX, double plotWidth, double fontSize)
    {
        int preferredTicks = Math.Max(1, chartData.XTickCount);
        for (int ticks = preferredTicks; ticks >= 1; ticks--)
        {
            double maxLabelWidth = 0;
            for (int i = 0; i <= ticks; i++)
            {
                float tickValue = minX + (maxX - minX) * i / ticks;
                Size labelSize = MeasureCanvasText(tickValue.ToString(chartData.XTickFormat, CultureInfo.InvariantCulture), fontSize);
                if (labelSize.Width > maxLabelWidth)
                {
                    maxLabelWidth = labelSize.Width;
                }
            }

            double spacing = plotWidth / ticks;
            if (spacing >= maxLabelWidth + 14)
            {
                return ticks;
            }
        }

        return 1;
    }

    private static Size MeasureCanvasText(string text, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize;
    }

    private ChartMargins GetChartMargins(Canvas canvas)
    {
        double left = ReferenceEquals(canvas, SensorSpectrumCanvas) ||
                      ReferenceEquals(canvas, SingleSensorWavelengthCanvas) ||
                      ReferenceEquals(canvas, SpectrumCanvas)
            ? WavelengthChartPlotLeft
            : ChartPlotLeft;
        double top = ReferenceEquals(canvas, WaveformCanvas) || ReferenceEquals(canvas, SingleSensorTemperatureCanvas)
            ? TemperatureChartPlotTop
            : ChartPlotTop;
        return new ChartMargins(left, ChartPlotRight, top, ChartPlotBottom);
    }

    private static Point GetAnchoredTextOrigin(double x, double y, Size size, CanvasTextAnchor anchor)
    {
        return anchor switch
        {
            CanvasTextAnchor.TopCenter => new Point(x - size.Width / 2, y),
            CanvasTextAnchor.TopRight => new Point(x - size.Width, y),
            CanvasTextAnchor.RightCenter => new Point(x - size.Width, y - size.Height / 2),
            CanvasTextAnchor.Center => new Point(x - size.Width / 2, y - size.Height / 2),
            _ => new Point(x, y)
        };
    }

    private static Brush BrushFromHex(string color) =>
        (Brush)new BrushConverter().ConvertFromString(color)!;

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(WaveformCanvas, _temperatureChartData, _temperatureViewport);
    }

    private void SensorSpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(SensorSpectrumCanvas, _sensorSpectrumChartData, _sensorSpectrumViewport);
    }

    private void SingleSensorWavelengthCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(SingleSensorWavelengthCanvas, _singleSensorWavelengthChartData, _singleSensorWavelengthViewport);
    }

    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(SpectrumCanvas, _spectrumChartData, _spectrumViewport);
    }

    private void SingleSensorTemperatureCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(SingleSensorTemperatureCanvas, _singleSensorTemperatureChartData, _singleSensorTemperatureViewport);
    }

    private void SingleSensorStrainCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(SingleSensorStrainCanvas, _singleSensorStrainChartData, _singleSensorStrainViewport);
    }

    private void StrainArrayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(StrainArrayCanvas, _strainArrayChartData, _strainArrayViewport);
    }

    private void ShapeReconstructionCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart(ShapeReconstructionCanvas, _shapeReconstructionChartData, _shapeReconstructionViewport);
    }

    private void ShowShapeChartZoomWindow(Canvas sourceCanvas)
    {
        ChartSeriesData? chartData = GetChartData(sourceCanvas);
        if (chartData is null)
        {
            return;
        }

        string titleText = GetChartZoomTitle(sourceCanvas);
        _shapeReconstructionZoomSourceCanvas = sourceCanvas;
        _shapeReconstructionZoomTitle = titleText;
        if (_shapeReconstructionZoomWindow is not null)
        {
            _shapeReconstructionZoomWindow.Title = titleText;
            if (_shapeReconstructionZoomTitleBlock is not null)
            {
                _shapeReconstructionZoomTitleBlock.Text = titleText;
            }

            _shapeReconstructionZoomWindow.Activate();
            DrawShapeReconstructionZoomChart();
            return;
        }

        _shapeReconstructionZoomViewport.Reset();
        var canvas = new Canvas
        {
            Background = BrushFromHex("#081A35"),
            ClipToBounds = true
        };
        canvas.SizeChanged += (_, _) => DrawShapeReconstructionZoomChart();
        canvas.MouseLeftButtonDown += ChartCanvas_MouseLeftButtonDown;
        canvas.MouseLeave += ChartCanvas_MouseLeave;
        canvas.MouseMove += ChartCanvas_MouseMove;
        canvas.MouseLeftButtonUp += ChartCanvas_MouseLeftButtonUp;
        canvas.MouseRightButtonDown += ChartCanvas_MouseRightButtonDown;

        var title = new TextBlock
        {
            Text = titleText,
            Foreground = BrushFromHex("#E9F5FF"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 14, 18, 8)
        };

        var border = new Border
        {
            Background = BrushFromHex("#081A35"),
            BorderBrush = BrushFromHex("#2E5A8F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(16),
            Padding = new Thickness(12),
            Child = canvas
        };

        var layout = new Grid
        {
            Background = BrushFromHex("#06162D")
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(title, 0);
        Grid.SetRow(border, 1);
        layout.Children.Add(title);
        layout.Children.Add(border);

        _shapeReconstructionZoomCanvas = canvas;
        _shapeReconstructionZoomTitleBlock = title;
        _shapeReconstructionZoomWindow = new Window
        {
            Title = titleText,
            Owner = this,
            Width = 1280,
            Height = 820,
            MinWidth = 900,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BrushFromHex("#06162D"),
            Content = layout
        };
        _shapeReconstructionZoomWindow.Closed += (_, _) =>
        {
            _shapeReconstructionZoomWindow = null;
            _shapeReconstructionZoomCanvas = null;
            _shapeReconstructionZoomSourceCanvas = null;
            _shapeReconstructionZoomTitleBlock = null;
            _shapeReconstructionZoomTitle = string.Empty;
            _shapeReconstructionZoomViewport.Reset();
        };
        _shapeReconstructionZoomWindow.Show();
        DrawShapeReconstructionZoomChart();
    }

    private void DrawShapeReconstructionZoomChart()
    {
        if (_shapeReconstructionZoomCanvas is null || _shapeReconstructionZoomSourceCanvas is null)
        {
            return;
        }

        DrawChart(_shapeReconstructionZoomCanvas, GetChartData(_shapeReconstructionZoomSourceCanvas), _shapeReconstructionZoomViewport);
    }

    private bool IsShapeSensingChartCanvas(Canvas canvas)
    {
        return ReferenceEquals(canvas, SingleSensorStrainCanvas) ||
            ReferenceEquals(canvas, StrainArrayCanvas) ||
            ReferenceEquals(canvas, ShapeReconstructionCanvas);
    }

    private string GetChartZoomTitle(Canvas canvas)
    {
        if (ReferenceEquals(canvas, SingleSensorStrainCanvas))
        {
            return "单光栅应变图";
        }

        if (ReferenceEquals(canvas, StrainArrayCanvas))
        {
            return "应变阵列图";
        }

        return GetSelectedShapeSensingMode() == ShapeSensingMode.DualFiber
            ? "二维形状重构"
            : "平面形状估计";
    }

    private void ChartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsShapeSensingChartCanvas(canvas) && e.ClickCount >= 2)
        {
            ShowShapeChartZoomWindow(canvas);
            e.Handled = true;
            return;
        }

        HideChartHoverToolTip();

        Point start = e.GetPosition(canvas);
        Rect plotArea = GetPlotArea(canvas);
        if (!plotArea.Contains(start))
        {
            return;
        }

        canvas.CaptureMouse();
        var selectionRect = new RectangleShape
        {
            Stroke = BrushFromHex("#7FD0FF"),
            Fill = new SolidColorBrush(Color.FromArgb(48, 103, 220, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            IsHitTestVisible = false
        };
        canvas.Children.Add(selectionRect);
        _chartSelectionState = new ChartSelectionState(canvas, start, selectionRect);
        e.Handled = true;
    }

    private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas)
        {
            return;
        }

        if (_chartSelectionState is not null)
        {
            if (!ReferenceEquals(_chartSelectionState.Canvas, canvas))
            {
                return;
            }

            HideChartHoverToolTip();
            Point current = ClampPointToPlot(e.GetPosition(canvas), canvas);
            _chartSelectionState.CurrentPoint = current;
            UpdateSelectionRectangle(_chartSelectionState);
            return;
        }

        UpdateChartHover(canvas, e.GetPosition(canvas));
    }

    private void ChartCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_chartSelectionState is null || sender is not Canvas canvas || !ReferenceEquals(_chartSelectionState.Canvas, canvas))
        {
            return;
        }

        Point end = ClampPointToPlot(e.GetPosition(canvas), canvas);
        canvas.ReleaseMouseCapture();

        if (canvas.Children.Contains(_chartSelectionState.SelectionRectangle))
        {
            canvas.Children.Remove(_chartSelectionState.SelectionRectangle);
        }

        ChartSelectionState state = _chartSelectionState;
        _chartSelectionState = null;

        Rect selection = new Rect(state.StartPoint, end);
        if (selection.Width < 12 || selection.Height < 12)
        {
            return;
        }

        if (!TryApplyZoomSelection(canvas, selection))
        {
            return;
        }

        RedrawChart(canvas);
        e.Handled = true;
    }

    private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        HideChartHoverToolTip();
    }

    private void ChartCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas)
        {
            return;
        }

        HideChartHoverToolTip();
        GetViewport(canvas).Reset();
        RedrawChart(canvas);
        e.Handled = true;
    }

    private void UpdateChartHover(Canvas canvas, Point mousePoint)
    {
        ChartSeriesData? data = GetChartData(canvas);
        if (data is null || !data.EnablePointHover)
        {
            HideChartHoverToolTip();
            return;
        }

        Rect plotArea = GetPlotArea(canvas);
        if (!plotArea.Contains(mousePoint) ||
            !TryGetVisibleDataRange(data, GetViewport(canvas), out _, out _, out _, out _, out float minX, out float maxX, out float minY, out float maxY))
        {
            HideChartHoverToolTip();
            return;
        }

        double plotWidth = Math.Max(1, plotArea.Width);
        double plotHeight = Math.Max(1, plotArea.Height);
        float xRange = Math.Max(0.0001f, maxX - minX);
        float yRange = Math.Max(0.0001f, maxY - minY);
        double threshold = 12.0;
        double bestDistanceSquared = threshold * threshold;
        (float X, float Y)? nearestPoint = null;

        int count = Math.Min(data.XAxis.Count, data.Values.Count);
        for (int i = 0; i < count; i++)
        {
            float xValue = data.XAxis[i];
            float yValue = data.Values[i];
            if (!float.IsFinite(xValue) || !float.IsFinite(yValue) || xValue < minX || xValue > maxX)
            {
                continue;
            }

            double screenX = plotArea.Left + ((xValue - minX) / xRange) * plotWidth;
            double normalized = (yValue - minY) / yRange;
            double screenY = plotArea.Top + (1.0 - normalized) * plotHeight;
            double dx = mousePoint.X - screenX;
            double dy = mousePoint.Y - screenY;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            nearestPoint = (xValue, yValue);
        }

        if (nearestPoint is null)
        {
            HideChartHoverToolTip();
            return;
        }

        canvas.ToolTip = _chartHoverToolTip;
        _chartHoverToolTip.Content =
            $"{data.XLabel}: {nearestPoint.Value.X.ToString(data.XTickFormat, CultureInfo.InvariantCulture)}\n" +
            $"{data.YLabel}: {nearestPoint.Value.Y.ToString(data.YTickFormat, CultureInfo.InvariantCulture)}";
        _chartHoverToolTip.IsOpen = true;
    }

    private void HideChartHoverToolTip()
    {
        if (_chartHoverToolTip.IsOpen)
        {
            _chartHoverToolTip.IsOpen = false;
        }
    }

    private bool TryApplyZoomSelection(Canvas canvas, Rect selection)
    {
        ChartSeriesData? data = GetChartData(canvas);
        ChartViewportState viewport = GetViewport(canvas);
        if (data is null || !TryGetVisibleDataRange(data, viewport, out _, out _, out _, out _, out float minX, out float maxX, out float minY, out float maxY))
        {
            return false;
        }

        Rect plotArea = GetPlotArea(canvas);
        double plotWidth = Math.Max(1, plotArea.Width);
        double plotHeight = Math.Max(1, plotArea.Height);

        double leftRatio = (selection.Left - plotArea.Left) / plotWidth;
        double rightRatio = (selection.Right - plotArea.Left) / plotWidth;
        double topRatio = (selection.Top - plotArea.Top) / plotHeight;
        double bottomRatio = (selection.Bottom - plotArea.Top) / plotHeight;

        float newMinX = minX + (float)(Math.Clamp(leftRatio, 0, 1) * (maxX - minX));
        float newMaxX = minX + (float)(Math.Clamp(rightRatio, 0, 1) * (maxX - minX));
        float newMaxY = maxY - (float)(Math.Clamp(topRatio, 0, 1) * (maxY - minY));
        float newMinY = maxY - (float)(Math.Clamp(bottomRatio, 0, 1) * (maxY - minY));

        if (newMaxX - newMinX < 0.001f || newMaxY - newMinY < 0.001f)
        {
            return false;
        }

        viewport.Set(newMinX, newMaxX, newMinY, newMaxY);
        return true;
    }

    private void UpdateSelectionRectangle(ChartSelectionState state)
    {
        Rect rect = new Rect(state.StartPoint, state.CurrentPoint);
        Canvas.SetLeft(state.SelectionRectangle, rect.Left);
        Canvas.SetTop(state.SelectionRectangle, rect.Top);
        state.SelectionRectangle.Width = rect.Width;
        state.SelectionRectangle.Height = rect.Height;
    }

    private Rect GetPlotArea(Canvas canvas)
    {
        ChartMargins margins = GetChartMargins(canvas);
        double plotWidth = Math.Max(10, canvas.ActualWidth - margins.Left - margins.Right);
        double plotHeight = Math.Max(10, canvas.ActualHeight - margins.Top - margins.Bottom);
        return new Rect(margins.Left, margins.Top, plotWidth, plotHeight);
    }

    private Point ClampPointToPlot(Point point, Canvas canvas)
    {
        Rect plotArea = GetPlotArea(canvas);
        double x = Math.Clamp(point.X, plotArea.Left, plotArea.Right);
        double y = Math.Clamp(point.Y, plotArea.Top, plotArea.Bottom);
        return new Point(x, y);
    }

    private void RedrawChart(Canvas canvas)
    {
        if (ReferenceEquals(canvas, SensorSpectrumCanvas))
        {
            DrawChart(SensorSpectrumCanvas, _sensorSpectrumChartData, _sensorSpectrumViewport);
        }
        else if (ReferenceEquals(canvas, SingleSensorWavelengthCanvas))
        {
            DrawChart(SingleSensorWavelengthCanvas, _singleSensorWavelengthChartData, _singleSensorWavelengthViewport);
        }
        else if (ReferenceEquals(canvas, SpectrumCanvas))
        {
            DrawChart(SpectrumCanvas, _spectrumChartData, _spectrumViewport);
        }
        else if (ReferenceEquals(canvas, SingleSensorTemperatureCanvas))
        {
            DrawChart(SingleSensorTemperatureCanvas, _singleSensorTemperatureChartData, _singleSensorTemperatureViewport);
        }
        else if (ReferenceEquals(canvas, WaveformCanvas))
        {
            DrawChart(WaveformCanvas, _temperatureChartData, _temperatureViewport);
        }
        else if (ReferenceEquals(canvas, SingleSensorStrainCanvas))
        {
            DrawChart(SingleSensorStrainCanvas, _singleSensorStrainChartData, _singleSensorStrainViewport);
        }
        else if (ReferenceEquals(canvas, StrainArrayCanvas))
        {
            DrawChart(StrainArrayCanvas, _strainArrayChartData, _strainArrayViewport);
        }
        else if (ReferenceEquals(canvas, ShapeReconstructionCanvas))
        {
            DrawChart(ShapeReconstructionCanvas, _shapeReconstructionChartData, _shapeReconstructionViewport);
        }
        else if (ReferenceEquals(canvas, _shapeReconstructionZoomCanvas))
        {
            DrawShapeReconstructionZoomChart();
        }
    }

    private ChartSeriesData? GetChartData(Canvas canvas)
    {
        if (ReferenceEquals(canvas, SensorSpectrumCanvas))
        {
            return _sensorSpectrumChartData;
        }

        if (ReferenceEquals(canvas, SingleSensorWavelengthCanvas))
        {
            return _singleSensorWavelengthChartData;
        }

        if (ReferenceEquals(canvas, SpectrumCanvas))
        {
            return _spectrumChartData;
        }

        if (ReferenceEquals(canvas, SingleSensorTemperatureCanvas))
        {
            return _singleSensorTemperatureChartData;
        }

        if (ReferenceEquals(canvas, WaveformCanvas))
        {
            return _temperatureChartData;
        }

        if (ReferenceEquals(canvas, SingleSensorStrainCanvas))
        {
            return _singleSensorStrainChartData;
        }

        if (ReferenceEquals(canvas, StrainArrayCanvas))
        {
            return _strainArrayChartData;
        }

        if (ReferenceEquals(canvas, ShapeReconstructionCanvas))
        {
            return _shapeReconstructionChartData;
        }

        if (ReferenceEquals(canvas, _shapeReconstructionZoomCanvas))
        {
            return _shapeReconstructionZoomSourceCanvas is not null
                ? GetChartData(_shapeReconstructionZoomSourceCanvas)
                : null;
        }

        return null;
    }

    private ChartViewportState GetViewport(Canvas canvas)
    {
        if (ReferenceEquals(canvas, SensorSpectrumCanvas))
        {
            return _sensorSpectrumViewport;
        }

        if (ReferenceEquals(canvas, SingleSensorWavelengthCanvas))
        {
            return _singleSensorWavelengthViewport;
        }

        if (ReferenceEquals(canvas, SpectrumCanvas))
        {
            return _spectrumViewport;
        }

        if (ReferenceEquals(canvas, SingleSensorTemperatureCanvas))
        {
            return _singleSensorTemperatureViewport;
        }

        if (ReferenceEquals(canvas, SingleSensorStrainCanvas))
        {
            return _singleSensorStrainViewport;
        }

        if (ReferenceEquals(canvas, StrainArrayCanvas))
        {
            return _strainArrayViewport;
        }

        if (ReferenceEquals(canvas, ShapeReconstructionCanvas))
        {
            return _shapeReconstructionViewport;
        }

        if (ReferenceEquals(canvas, _shapeReconstructionZoomCanvas))
        {
            return _shapeReconstructionZoomViewport;
        }

        return _temperatureViewport;
    }

    private void ResetChartViewports()
    {
        _sensorSpectrumViewport.Reset();
        _singleSensorWavelengthViewport.Reset();
        _spectrumViewport.Reset();
        _singleSensorTemperatureViewport.Reset();
        _singleSensorStrainViewport.Reset();
        _strainArrayViewport.Reset();
        _shapeReconstructionViewport.Reset();
        _shapeReconstructionZoomViewport.Reset();
        _temperatureViewport.Reset();
    }

    private void SetSelectedChannelControls(ChannelOption option)
    {
        if (option.IsAllChannels)
        {
            _monitorAllChannels = true;
        }
        else
        {
            _monitorAllChannels = false;
            _selectedMonitorChannel = option.ChannelIndex;
        }

        _isSynchronizingGraphSelection = true;
        try
        {
            if (!ReferenceEquals(ChannelListBox.SelectedItem, option))
            {
                ChannelListBox.SelectedItem = option;
            }
        }
        finally
        {
            _isSynchronizingGraphSelection = false;
        }

        UpdateCurrentMonitorChannelDisplay();
        SyncSelectedChannelParameterInputs();
        ApplyAlarmSettingsToService();
    }

    private void SyncSelectedChannelParameterInputs(bool persist = false)
    {
        int channelIndex = Math.Clamp(GetSelectedMonitorChannelIndex(), 0, MaxMonitorChannels - 1);
        ParameterChannelSettingItem setting = GetOrCreateParameterChannelSetting(channelIndex);

        foreach (ParameterChannelSettingItem item in _parameterChannelSettings)
        {
            item.IsSelected = item.ChannelIndex == channelIndex;
        }

        string displayChannel = (channelIndex + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        if (ChannelTextBox.Text != displayChannel)
        {
            ChannelTextBox.Text = displayChannel;
        }

        if (CenterWavelengthTextBox.Text != setting.CenterWavelengthText)
        {
            CenterWavelengthTextBox.Text = setting.CenterWavelengthText;
        }

        if (persist && !_isRestoringUiState)
        {
            SaveUiState();
        }
    }

    private void ParameterChannelEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ParameterChannelSettingItem item)
        {
            return;
        }

        if (item.ChannelIndex == GetSelectedMonitorChannelIndex())
        {
            SyncSelectedChannelParameterInputs(persist: true);
        }
        else if (!_isRestoringUiState)
        {
            SaveUiState();
        }

        if (_calibrationWindow is not null)
        {
            _calibrationWindow.UpdateParameters(BuildCalibrationWindowParameters(GetSelectedMonitorChannelIndex()));
        }
    }

    private void ParameterChannelCenterWavelengths_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ParameterChannelSettingItem item)
        {
            return;
        }

        if (item.ChannelIndex == GetSelectedMonitorChannelIndex())
        {
            SyncSelectedChannelParameterInputs(persist: true);
        }
        else if (!_isRestoringUiState)
        {
            SaveUiState();
        }
    }

    private void OpticSwitchEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OpticSwitchEnabledTextBox is null || OpticSwitchEnabledCheckBox is null)
        {
            return;
        }

        OpticSwitchEnabledTextBox.Text = OpticSwitchEnabledCheckBox.IsChecked == true ? "1" : "0";
        if (!_isRestoringUiState)
        {
            SaveUiState();
        }

        if (_calibrationWindow is not null)
        {
            _calibrationWindow.UpdateParameters(BuildCalibrationWindowParameters(GetSelectedMonitorChannelIndex()));
        }
    }

    private bool ValidateChannelSelectionBeforeSave(bool showMessageBox, out string message)
    {
        message = string.Empty;
        List<int> enabledChannels = GetEnabledParameterChannelIndexes();

        if (enabledChannels.Count == 0)
        {
            message = "请至少勾选一个通道。";
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "采集参数", message);
            }

            return false;
        }

        if (OpticSwitchEnabledCheckBox?.IsChecked != true && enabledChannels.Count > 1)
        {
            message = "未启用多通道开关时，只能勾选一个通道。";
            if (showMessageBox)
            {
                AppMessageDialog.ShowInfo(this, "采集参数", message);
            }

            return false;
        }

        return true;
    }

    private void MultiWaveReverseCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (MultiWaveReverseTextBox is null || MultiWaveReverseCheckBox is null)
        {
            return;
        }

        MultiWaveReverseTextBox.Text = MultiWaveReverseCheckBox.IsChecked == true ? "1" : "0";
        if (!_isRestoringUiState)
        {
            SaveUiState();
        }
    }

    private void SetSelectedSensorRow(SensorInfoRow? row, bool ensureVisible = false)
    {
        _isSynchronizingGraphSelection = true;
        try
        {
            if (row is null)
            {
                SensorInfoGrid.SelectedIndex = -1;
            }
            else
            {
                SensorInfoGrid.SelectedItem = row;
            }
        }
        finally
        {
            _isSynchronizingGraphSelection = false;
        }

        if (row is not null && ensureVisible)
        {
            void EnsureVisible()
            {
                SensorInfoGrid.UpdateLayout();
                SensorInfoGrid.ScrollIntoView(row);
                if (SensorInfoGrid.Columns.Count > 0)
                {
                    SensorInfoGrid.ScrollIntoView(row, SensorInfoGrid.Columns[0]);
                }
            }

            EnsureVisible();
            Dispatcher.BeginInvoke(new Action(EnsureVisible), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(EnsureVisible), DispatcherPriority.ApplicationIdle);
        }
    }

    private (double HorizontalOffset, double VerticalOffset)? CaptureDataGridScrollOffsets(DataGrid grid)
    {
        ScrollViewer? viewer = FindVisualChild<ScrollViewer>(grid);
        if (viewer is null)
        {
            return null;
        }

        return (viewer.HorizontalOffset, viewer.VerticalOffset);
    }

    private void RestoreDataGridScrollOffsets(
        DataGrid grid,
        (double HorizontalOffset, double VerticalOffset)? offsets)
    {
        if (offsets is null)
        {
            return;
        }

        var state = offsets.Value;

        void RestoreCore()
        {
            grid.UpdateLayout();
            ScrollViewer? viewer = FindVisualChild<ScrollViewer>(grid);
            if (viewer is null)
            {
                return;
            }

            viewer.ScrollToHorizontalOffset(Math.Clamp(state.HorizontalOffset, 0, viewer.ScrollableWidth));
            viewer.ScrollToVerticalOffset(Math.Clamp(state.VerticalOffset, 0, viewer.ScrollableHeight));
        }

        Dispatcher.BeginInvoke(new Action(RestoreCore), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(RestoreCore), DispatcherPriority.ApplicationIdle);
    }

    private static bool TryGetVisibleDataRange(
        ChartSeriesData data,
        ChartViewportState viewport,
        out float dataMinX,
        out float dataMaxX,
        out float dataMinY,
        out float dataMaxY,
        out float minX,
        out float maxX,
        out float minY,
        out float maxY)
    {
        dataMinX = dataMaxX = dataMinY = dataMaxY = minX = maxX = minY = maxY = 0f;

        int count = Math.Min(data.XAxis.Count, data.Values.Count);
        if (count <= 0)
        {
            return false;
        }

        var validPoints = new List<(float X, float Y)>(count);
        for (int i = 0; i < count; i++)
        {
            float x = data.XAxis[i];
            float y = data.Values[i];
            if (float.IsFinite(x) && float.IsFinite(y))
            {
                validPoints.Add((x, y));
            }
        }

        if (validPoints.Count == 0)
        {
            return false;
        }

        dataMinX = validPoints.Min(p => p.X);
        dataMaxX = validPoints.Max(p => p.X);
        dataMinY = validPoints.Min(p => p.Y);
        dataMaxY = validPoints.Max(p => p.Y);

        float fullMinX = ResolveRangeMin(data.DefaultMinX, dataMinX);
        float fullMaxX = ResolveRangeMax(data.DefaultMaxX, dataMaxX, fullMinX);
        float fullMinY = ResolveRangeMin(data.DefaultMinY, dataMinY);
        float fullMaxY = ResolveRangeMax(data.DefaultMaxY, dataMaxY, fullMinY);

        if (!viewport.TryGetEffectiveRange(fullMinX, fullMaxX, fullMinY, fullMaxY, out minX, out maxX, out minY, out maxY))
        {
            minX = fullMinX;
            maxX = fullMaxX;
            minY = fullMinY;
            maxY = fullMaxY;
        }

        return true;
    }

    private void ReindexRealtimeRows()
    {
        for (int i = 0; i < _realtimeAlarmRows.Count; i++)
        {
            _realtimeAlarmRows[i].Seq = i + 1;
        }
    }

    private void AddRuntimeLog(string content)
    {
        _runtimeLogItems.Insert(0, new RuntimeLogItem
        {
            Seq = _runtimeLogItems.Count + 1,
            TimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Content = content
        });

        if (_runtimeLogItems.Count > 500)
        {
            _runtimeLogItems.RemoveAt(_runtimeLogItems.Count - 1);
        }

        for (int i = 0; i < _runtimeLogItems.Count; i++)
        {
            _runtimeLogItems[i].Seq = _runtimeLogItems.Count - i;
        }
    }

    private void OpenAcquisitionParameterPage_Click(object sender, RoutedEventArgs e)
    {
        if (ParameterDialogOverlay is not null)
        {
            ParameterDialogOverlay.Visibility = Visibility.Collapsed;
        }

        SyncSelectedChannelParameterInputs();
        EnsureCoefficientContextForSelectedMonitorChannel(suppressLog: true);
        SyncAcquisitionParameterSelectorsFromTextValues();
        UpdateComputedProfilePoints();
        if (AcquisitionParameterDialogOverlay is not null)
        {
            AcquisitionParameterDialogOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OpenParameterDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (AcquisitionParameterDialogOverlay is not null)
        {
            AcquisitionParameterDialogOverlay.Visibility = Visibility.Collapsed;
        }

        BeginAlarmDialogSession();
        EnsureZoneChannelSelection();
        LoadZoneAlarmEditorStateForSelectedChannel();
        ParameterDialogOverlay.Visibility = Visibility.Visible;
        ResetZoneGridScrollPosition();
    }

    private void OpenRuntimeLogDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeLogDialogOverlay is not null)
        {
            RuntimeLogDialogOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CloseAcquisitionParameterDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (AcquisitionParameterDialogOverlay is not null)
        {
            AcquisitionParameterDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseRuntimeLogDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeLogDialogOverlay is not null)
        {
            RuntimeLogDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseParameterDialogButton_Click(object sender, RoutedEventArgs e)
    {
        EndAlarmDialogSession(commit: false);
        ParameterDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void ResetZoneGridScrollPosition()
    {
        void ResetCore()
        {
            ZoneConfigGrid.UpdateLayout();
            if (_zoneParameterItems.Count > 0 && ZoneConfigGrid.Columns.Count > 0)
            {
                ZoneConfigGrid.SelectedIndex = 0;
                ZoneConfigGrid.CurrentCell = new DataGridCellInfo(_zoneParameterItems[0], ZoneConfigGrid.Columns[0]);
                ZoneConfigGrid.ScrollIntoView(_zoneParameterItems[0], ZoneConfigGrid.Columns[0]);
            }

            foreach (ScrollViewer viewer in FindVisualChildren<ScrollViewer>(ZoneConfigGrid))
            {
                viewer.ScrollToHome();
                viewer.ScrollToHorizontalOffset(0);
                viewer.ScrollToVerticalOffset(0);
            }
        }

        Dispatcher.BeginInvoke(new Action(ResetCore), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(ResetCore), DispatcherPriority.ApplicationIdle);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void InitializeZoneRows(int zoneCount, int zoneLength)
    {
        _zoneParameterItems.Clear();
        AppendZoneRows(zoneCount, zoneLength);
    }

    private void ApplyZonePresetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyParameterDialogState(closeAfterApply: false);
    }

    private void AddZoneRowButton_Click(object sender, RoutedEventArgs e)
    {
        int zoneCount = Math.Clamp(ParseInt(ZoneCountTextBox.Text, 10), 1, 200);
        int zoneLength = Math.Max(1, ParseInt(ZoneLengthTextBox.Text, 10));
        ZoneCountTextBox.Text = zoneCount.ToString(CultureInfo.InvariantCulture);
        ZoneLengthTextBox.Text = zoneLength.ToString(CultureInfo.InvariantCulture);

        AppendZoneRows(zoneCount, zoneLength);
        AddRuntimeLog($"已追加 {zoneCount} 个分区。");
    }

    private void DeleteZoneRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (ZoneConfigGrid.SelectedItem is ZoneParameterItem selected)
        {
            _zoneParameterItems.Remove(selected);
        }
        else if (_zoneParameterItems.Count > 0)
        {
            _zoneParameterItems.RemoveAt(_zoneParameterItems.Count - 1);
        }

        for (int i = 0; i < _zoneParameterItems.Count; i++)
        {
            _zoneParameterItems[i].ZoneNo = i + 1;
            _zoneParameterItems[i].Description = $"分区{i + 1}";
        }
    }

    private void ClearZoneRowsButton_Click(object sender, RoutedEventArgs e)
    {
        _zoneParameterItems.Clear();
    }

    private void ConfirmZoneConfigButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyParameterDialogState(closeAfterApply: true);
    }

    private void CalculateTempCorrectionButton_Click(object sender, RoutedEventArgs e)
    {
        TempCorrectionTextBox.Text = "0.0";
        AddRuntimeLog("已执行修正值计算（占位实现）。");
    }

    private void WriteTempCorrectionButton_Click(object sender, RoutedEventArgs e)
    {
        AddRuntimeLog($"温度修正值已写入：{TempCorrectionTextBox.Text.Trim()}");
    }

    private void AppendZoneRows(int zoneCount, int zoneLength)
    {
        int start = _zoneParameterItems.Count == 0 ? 1 : _zoneParameterItems[^1].EndPos + 1;
        int baseIndex = _zoneParameterItems.Count;
        for (int i = 0; i < zoneCount; i++)
        {
            int zoneNo = baseIndex + i + 1;
            int end = start + zoneLength - 1;
            _zoneParameterItems.Add(new ZoneParameterItem
            {
                ZoneNo = zoneNo,
                Description = $"分区{zoneNo}",
                StartPos = start,
                EndPos = end,
                AlarmLevel1 = 0.0,
                DiffTempAlarm = 0.0
            });
            start = end + 1;
        }
    }

    private void MigrateLegacyZoneThresholdDefaults()
    {
        foreach (ZoneParameterItem zone in _zoneParameterItems)
        {
            if (zone.AlarmLevel1 == 70.0 &&
                zone.DiffTempAlarm == 10.0)
            {
                zone.AlarmLevel1 = 60.0;
            }
        }
    }

    private static void NormalizeZoneThresholds(IEnumerable<ZoneParameterItem> zones)
    {
        foreach (ZoneParameterItem zone in zones)
        {
            zone.AlarmLevel1 = NormalizeThreshold(zone.AlarmLevel1, FixedTempThresholdOptions, 0d);
            zone.DiffTempAlarm = NormalizeThreshold(zone.DiffTempAlarm, RateThresholdOptions, 0d);
        }
    }

    private static double NormalizeThreshold(double value, IReadOnlyList<double> options, double fallback)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            return fallback;
        }

        double best = options[0];
        double bestDistance = Math.Abs(best - value);
        for (int i = 1; i < options.Count; i++)
        {
            double distance = Math.Abs(options[i] - value);
            if (distance < bestDistance)
            {
                best = options[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    private void ApplyParameterDialogState(bool closeAfterApply)
    {
        ZoneConfigGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ZoneConfigGrid.CommitEdit(DataGridEditingUnit.Row, true);

        int zoneCount = Math.Clamp(ParseInt(ZoneCountTextBox.Text, 0), 0, 200);
        int zoneLength = Math.Clamp(ParseInt(ZoneLengthTextBox.Text, 0), 0, 100000);
        ZoneCountTextBox.Text = zoneCount > 0 ? zoneCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ZoneLengthTextBox.Text = zoneLength > 0 ? zoneLength.ToString(CultureInfo.InvariantCulture) : string.Empty;

        NormalizeZoneThresholds(_zoneParameterItems);

        _appliedParameterState.EnableAlarmL1 = EnableAlarmL1CheckBox.IsChecked == true;
        _appliedParameterState.EnableDiffAlarm = EnableDiffAlarmCheckBox.IsChecked == true;

        _appliedParameterState.ZoneCount = zoneCount;
        _appliedParameterState.ZoneLength = zoneLength;
        _appliedParameterState.ZoneRows = _zoneParameterItems.Count;
        _appliedParameterState.TempCorrection = ParseFloat(TempCorrectionTextBox.Text, 0f);
        _appliedParameterState.ChannelText = GetSelectedZoneChannelText();
        _appliedZoneParameterItems = CloneZoneParameterItems(_zoneParameterItems);
        PersistCurrentZoneAlarmEditorState();
        List<int> modifiedChannels = GetModifiedAlarmChannelIndices();
        EndAlarmDialogSession(commit: true);
        ApplyAlarmSettingsToService();
        SaveUiState();

        string logContent;
        if (modifiedChannels.Count == 0)
        {
            logContent = closeAfterApply
                ? "高级参数已确认：未检测到通道配置变更。"
                : "高级参数已应用：未检测到通道配置变更，窗口保持打开。";
        }
        else
        {
            string channelText = FormatAlarmChannelList(modifiedChannels);
            int totalRows = modifiedChannels
                .Where(_alarmChannelStatesByChannel.ContainsKey)
                .Sum(channel => _alarmChannelStatesByChannel[channel].ZoneRows.Count);

            logContent = closeAfterApply
                ? $"高级参数已应用并确认：变更通道={channelText}，分区行数={totalRows}。"
                : $"高级参数已应用：变更通道={channelText}，分区行数={totalRows}，窗口保持打开。";
        }

        AddRuntimeLog(logContent);

        if (closeAfterApply)
        {
            ParameterDialogOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            BeginAlarmDialogSession();
        }
    }

    private void SyncAppliedAlarmStateFromUi()
    {
        NormalizeZoneThresholds(_zoneParameterItems);

        _appliedParameterState.EnableAlarmL1 = EnableAlarmL1CheckBox.IsChecked == true;
        _appliedParameterState.EnableDiffAlarm = EnableDiffAlarmCheckBox.IsChecked == true;
        _appliedParameterState.ZoneCount = Math.Clamp(ParseInt(ZoneCountTextBox.Text, 0), 0, 200);
        _appliedParameterState.ZoneLength = Math.Clamp(ParseInt(ZoneLengthTextBox.Text, 0), 0, 100000);
        _appliedParameterState.ZoneRows = _zoneParameterItems.Count;
        _appliedParameterState.TempCorrection = ParseFloat(TempCorrectionTextBox.Text, 0f);
        _appliedParameterState.ChannelText = GetSelectedZoneChannelText();
        _appliedZoneParameterItems = CloneZoneParameterItems(_zoneParameterItems);
    }

    private void ApplyAlarmSettingsToService()
    {
        _service?.UpdateAlarmSettings(BuildAlarmSettingsFromAppliedState());
    }

    private AlarmSettingsModel BuildAlarmSettingsFromAppliedState()
    {
        return new AlarmSettingsModel
        {
            Channels = _alarmChannelStatesByChannel
                .OrderBy(x => x.Key)
                .Select(x =>
                {
                    _loadedCoefficientProfilesByChannel.TryGetValue(x.Key, out LoadedCoefficientProfile? profile);
                    return new AlarmChannelSettingsModel
                    {
                        Channel = x.Key,
                        EnableAlarmL1 = x.Value.EnableAlarmL1,
                        EnableDiffAlarm = x.Value.EnableDiffAlarm,
                        TempCorrectionC = x.Value.TempCorrection,
                        SourceSensorIndexes = Array.Empty<int>(),
                        SensorPositionsM = profile?.DisplaySensorPositionsM.ToArray() ?? Array.Empty<float>(),
                        TempSensitivityPmPerC = profile?.TempSensitivityPmPerC.ToArray() ?? Array.Empty<float>(),
                        ReferenceTemperaturesC = profile?.ReferenceTemperaturesC.ToArray() ?? Array.Empty<float>(),
                        ReferenceWavelengthsNm = profile?.ReferenceWavelengthsNm.ToArray() ?? Array.Empty<float>(),
                        Zones = x.Value.ZoneRows
                            .Where(zone => zone.EndPos >= zone.StartPos &&
                                           (zone.AlarmLevel1 > 0d || zone.DiffTempAlarm > 0d))
                            .Select(CloneZone)
                            .ToArray()
                    };
                })
                .ToArray()
        };
    }

    private static List<ZoneParameterItem> CloneZoneParameterItems(IEnumerable<ZoneParameterItem> zones)
    {
        return zones
            .Select(zone => new ZoneParameterItem
            {
                ZoneNo = zone.ZoneNo,
                Description = zone.Description,
                StartPos = zone.StartPos,
                EndPos = zone.EndPos,
                AlarmLevel1 = zone.AlarmLevel1,
                DiffTempAlarm = zone.DiffTempAlarm
            })
            .ToList();
    }

    private string GetSelectedZoneChannelText()
    {
        if (ZoneChannelComboBox.SelectedItem is ChannelOption item &&
            item.DisplayText is string text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return string.Empty;
    }

    private static int ParseUiChannelToSdkIndex(string? text, int fallback)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int displayChannel))
        {
            return fallback;
        }

        if (displayChannel <= 0)
        {
            return fallback;
        }

        return displayChannel - DisplayChannelBase;
    }

    private static int ParseZoneChannelText(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        Match match = Regex.Match(text, @"(\d+)");
        if (!match.Success)
        {
            return fallback;
        }

        return ParseUiChannelToSdkIndex(match.Groups[1].Value, fallback);
    }

    private static bool TryParseCoefficientFileChannel(string? path, out int sdkChannelIndex)
    {
        sdkChannelIndex = -1;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fileNameWithoutExtension = IoPath.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return false;
        }

        Match match = Regex.Match(fileNameWithoutExtension, @"(\d+)$");
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int displayChannel))
        {
            return false;
        }

        if (displayChannel < DisplayChannelBase)
        {
            return false;
        }

        int resolvedChannel = displayChannel - DisplayChannelBase;
        if (resolvedChannel < 0 || resolvedChannel >= MaxMonitorChannels)
        {
            return false;
        }

        sdkChannelIndex = resolvedChannel;
        return true;
    }

    private static int ResolveCoefficientFileChannel(string path, int fallbackChannel)
    {
        if (TryParseCoefficientFileChannel(path, out int parsedChannel))
        {
            return parsedChannel;
        }

        return Math.Clamp(fallbackChannel, 0, MaxMonitorChannels - 1);
    }

    private bool TryValidateCoefficientFileChannel(string path, int selectedChannel, out string message)
    {
        if (!IsCoefficientFileInAllowedDirectory(path))
        {
            message = $"系统系数文件只允许放在程序目录：{AppDomain.CurrentDomain.BaseDirectory}";
            return false;
        }

        string fileName = IoPath.GetFileName(path);
        if (!TryParseCoefficientFileChannel(path, out int fileChannel))
        {
            message = $"系数文件名必须以通道号结尾，例如“系统系数文件2.csv”。当前文件“{fileName}”无法解析通道号。";
            return false;
        }

        if (fileChannel != selectedChannel)
        {
            message = $"当前监控通道是 {FormatChannelLabel(selectedChannel)}，但文件“{fileName}”对应 {FormatChannelLabel(fileChannel)}。请先在监控页切换到对应通道后再加载。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool IsCoefficientFileInAllowedDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath = IoPath.GetFullPath(path);
        return IsPathUnderDirectory(fullPath, AppDomain.CurrentDomain.BaseDirectory);
    }

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        char[] invalidChars = IoPath.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string fullDirectory = IoPath.GetFullPath(directory)
            .TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar) + IoPath.DirectorySeparatorChar;
        string fullPath = IoPath.GetFullPath(path);
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private int GetDefaultMonitorChannelFromState(UiStateSnapshot state)
    {
        if (TryParseCoefficientFileChannel(state.CoefficientFilePath, out int pathChannel))
        {
            return pathChannel;
        }

        if (_coefficientFilePathsByChannel.Count > 0)
        {
            return _coefficientFilePathsByChannel.Keys.Min();
        }

        return 0;
    }

    private static string FormatChannelLabel(int sdkChannelIndex)
    {
        return $"通道{sdkChannelIndex + DisplayChannelBase}";
    }

    private sealed class RealtimeAlarmRow : INotifyPropertyChanged
    {
        private int _seq;

        public int Seq
        {
            get => _seq;
            set
            {
                if (_seq == value)
                {
                    return;
                }

                _seq = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seq)));
            }
        }

        public string TimeText { get; set; } = string.Empty;
        public int ChannelIndex { get; set; }
        public int SensorIndex { get; set; }
        public string ChannelText { get; set; } = string.Empty;
        public string TypeText { get; set; } = string.Empty;
        public float PositionM { get; set; }
        public string ChannelDisplayText => (ChannelIndex + DisplayChannelBase).ToString(CultureInfo.InvariantCulture);
        public string PositionText => float.IsFinite(PositionM) ? PositionM.ToString("F1") : "--";
        public string RealtimeTypeText => GetCompactAlarmTypeText(TypeText);

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string GetCompactAlarmTypeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "--";
            }

            return text
                .Replace("报警", string.Empty, StringComparison.Ordinal)
                .Replace("异常", string.Empty, StringComparison.Ordinal)
                .Trim();
        }
    }

    private sealed class RealtimeAlarmRowState
    {
        public string TimeText { get; set; } = string.Empty;
        public int ChannelIndex { get; set; }
        public string ChannelText { get; set; } = string.Empty;
        public int SensorIndex { get; set; }
        public string TypeText { get; set; } = string.Empty;
        public float PositionM { get; set; }
    }

    private sealed class DeviceRuntimeCache
    {
        public SnapshotModel? LastSnapshot { get; set; }
        public Dictionary<int, SnapshotModel> SnapshotsByChannel { get; init; } = new();
        public Dictionary<int, long> LastSnapshotTimestampMsByChannel { get; init; } = new();
        public Dictionary<int, double> SnapshotFrequencyHzByChannel { get; init; } = new();
        public long LastSnapshotTimestampMsOverall { get; set; }
        public double SnapshotFrequencyHzOverall { get; set; }
        public Dictionary<(int Channel, int SensorIndex), List<float>> SingleSensorTemperatureTrendByKey { get; init; } = new();
        public Dictionary<(int Channel, int SensorIndex), List<float>> SingleSensorWavelengthTrendByKey { get; init; } = new();
    }

    private sealed class RuntimeLogItem
    {
        public int Seq { get; set; }
        public string TimeText { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChannelOption
    {
        public ChannelOption(int channelIndex, bool isAllChannels = false)
        {
            ChannelIndex = channelIndex;
            IsAllChannels = isAllChannels;
        }

        public static ChannelOption CreateAllChannels() => new(-1, true);

        public int ChannelIndex { get; }
        public bool IsAllChannels { get; }
        public string DisplayText => IsAllChannels ? "所有通道" : FormatChannelLabel(ChannelIndex);

        public override string ToString() => DisplayText;
    }

    private sealed class ParameterChannelSettingItem : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private string _centerWavelengthText = string.Empty;
        private bool _isSelected;

        public ParameterChannelSettingItem(int channelIndex)
        {
            ChannelIndex = channelIndex;
        }

        public int ChannelIndex { get; }
        public string ChannelLabel => $"CH{ChannelIndex + DisplayChannelBase}";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public string CenterWavelengthText
        {
            get => _centerWavelengthText;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(_centerWavelengthText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _centerWavelengthText = normalized;
                OnPropertyChanged(nameof(CenterWavelengthText));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class SensorInfoRow
    {
        public SensorInfoRow(int sensorIndex, float positionM, float wavelengthNm, float temperatureC, float strainMicro, SensorInfoDisplayMode displayMode)
        {
            SensorIndex = sensorIndex;
            PositionM = positionM;
            WavelengthNm = wavelengthNm;
            TemperatureC = temperatureC;
            StrainMicro = strainMicro;
            DisplayMode = displayMode;
        }

        public int SensorIndex { get; }
        public int Seq => SensorIndex + 1;
        public float PositionM { get; }
        public float WavelengthNm { get; }
        public float TemperatureC { get; }
        public float StrainMicro { get; }
        private SensorInfoDisplayMode DisplayMode { get; }
        public string PositionText => PositionM.ToString("F1", CultureInfo.InvariantCulture);
        public string WavelengthText => float.IsFinite(WavelengthNm)
            ? WavelengthNm.ToString("F4", CultureInfo.InvariantCulture)
            : "--";
        public string TemperatureText => float.IsFinite(TemperatureC)
            ? TemperatureC.ToString("F2", CultureInfo.InvariantCulture)
            : "--";
        public string StrainText => float.IsFinite(StrainMicro)
            ? StrainMicro.ToString("F2", CultureInfo.InvariantCulture)
            : "--";
        public string DisplayValueText => DisplayMode switch
        {
            SensorInfoDisplayMode.Temperature => TemperatureText,
            SensorInfoDisplayMode.Strain => StrainText,
            _ => WavelengthText
        };
    }

    private sealed class ChartSeriesData
    {
        public ChartSeriesData(
            IReadOnlyList<float> xAxis,
            IReadOnlyList<float> values,
            string xLabel,
            string yLabel,
            Brush lineBrush,
            bool showMarkers = false,
            double markerDiameter = 5,
            Brush? markerBrush = null,
            int? markerRenderLimit = null,
            bool enablePointHover = false,
            float? defaultMinX = null,
            float? defaultMaxX = null,
            float? defaultMinY = null,
            float? defaultMaxY = null,
            string xTickFormat = "F1",
            string yTickFormat = "F2",
            int xTickCount = 6,
            int yTickCount = 5,
            bool showZeroLine = false)
        {
            XAxis = xAxis;
            Values = values;
            XLabel = xLabel;
            YLabel = yLabel;
            LineBrush = lineBrush;
            ShowMarkers = showMarkers;
            MarkerDiameter = markerDiameter;
            MarkerBrush = markerBrush ?? lineBrush;
            MarkerRenderLimit = markerRenderLimit;
            EnablePointHover = enablePointHover;
            DefaultMinX = defaultMinX;
            DefaultMaxX = defaultMaxX;
            DefaultMinY = defaultMinY;
            DefaultMaxY = defaultMaxY;
            XTickFormat = xTickFormat;
            YTickFormat = yTickFormat;
            XTickCount = xTickCount;
            YTickCount = yTickCount;
            ShowZeroLine = showZeroLine;
        }

        public IReadOnlyList<float> XAxis { get; }
        public IReadOnlyList<float> Values { get; }
        public string XLabel { get; }
        public string YLabel { get; }
        public Brush LineBrush { get; }
        public bool ShowMarkers { get; }
        public double MarkerDiameter { get; }
        public Brush MarkerBrush { get; }
        public int? MarkerRenderLimit { get; }
        public bool EnablePointHover { get; }
        public float? DefaultMinX { get; }
        public float? DefaultMaxX { get; }
        public float? DefaultMinY { get; }
        public float? DefaultMaxY { get; }
        public string XTickFormat { get; }
        public string YTickFormat { get; }
        public int XTickCount { get; }
        public int YTickCount { get; }
        public bool ShowZeroLine { get; }
    }

    private sealed class ChartViewportState
    {
        public bool HasCustomRange { get; private set; }
        public float MinX { get; private set; }
        public float MaxX { get; private set; }
        public float MinY { get; private set; }
        public float MaxY { get; private set; }

        public void Set(float minX, float maxX, float minY, float maxY)
        {
            MinX = Math.Min(minX, maxX);
            MaxX = Math.Max(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxY = Math.Max(minY, maxY);
            HasCustomRange = true;
        }

        public void Reset()
        {
            HasCustomRange = false;
            MinX = MaxX = MinY = MaxY = 0f;
        }

        public bool TryGetEffectiveRange(
            float dataMinX,
            float dataMaxX,
            float dataMinY,
            float dataMaxY,
            out float minX,
            out float maxX,
            out float minY,
            out float maxY)
        {
            if (!HasCustomRange)
            {
                minX = dataMinX;
                maxX = dataMaxX;
                minY = dataMinY;
                maxY = dataMaxY;
                return true;
            }

            minX = Math.Clamp(MinX, dataMinX, dataMaxX);
            maxX = Math.Clamp(MaxX, dataMinX, dataMaxX);
            minY = Math.Clamp(MinY, dataMinY, dataMaxY);
            maxY = Math.Clamp(MaxY, dataMinY, dataMaxY);

            if (maxX - minX < 0.0001f || maxY - minY < 0.0001f)
            {
                return false;
            }

            return true;
        }
    }

    private sealed class ChartSelectionState
    {
        public ChartSelectionState(Canvas canvas, Point startPoint, RectangleShape selectionRectangle)
        {
            Canvas = canvas;
            StartPoint = startPoint;
            CurrentPoint = startPoint;
            SelectionRectangle = selectionRectangle;
        }

        public Canvas Canvas { get; }
        public Point StartPoint { get; }
        public Point CurrentPoint { get; set; }
        public RectangleShape SelectionRectangle { get; }
    }

    private readonly record struct ChartMargins(double Left, double Right, double Top, double Bottom);

    private enum CanvasTextAnchor
    {
        TopLeft,
        TopCenter,
        TopRight,
        RightCenter,
        Center
    }

    private sealed class AppliedParameterState
    {
        public bool EnableAlarmL1 { get; set; }
        public bool EnableDiffAlarm { get; set; }
        public int ZoneCount { get; set; }
        public int ZoneLength { get; set; }
        public int ZoneRows { get; set; }
        public float TempCorrection { get; set; }
        public string ChannelText { get; set; } = string.Empty;
    }

    private sealed class AlarmChannelEditorState
    {
        public bool EnableAlarmL1 { get; set; }
        public bool EnableDiffAlarm { get; set; }
        public int ZoneCount { get; set; }
        public int ZoneLength { get; set; }
        public float TempCorrection { get; set; }
        public List<ZoneParameterItem> ZoneRows { get; set; } = new();
    }

    private sealed class AlarmChannelStateSnapshot
    {
        public bool EnableAlarmL1 { get; set; }
        public bool EnableDiffAlarm { get; set; }
        public int ZoneCount { get; set; }
        public int ZoneLength { get; set; }
        public float TempCorrection { get; set; }
        public List<ZoneParameterItem>? ZoneRows { get; set; }
    }

    private sealed class UiStateSnapshot
    {
        public string? Ip { get; set; }
        public string? Channel { get; set; }
        public string? StartWavelength { get; set; }
        public string? StopWavelength { get; set; }
        public string? FiberLength { get; set; }
        public string? ProfileStep { get; set; }
        public string? TargetPoints { get; set; }
        public string? Delay { get; set; }
        public string? PulseWidth { get; set; }
        public string? OpticSwitchEnabled { get; set; }
        public string? EdfaCurrent { get; set; }
        public string? EdfaPaCurrent { get; set; }
        public string? CalibrationEdfaCurrent { get; set; }
        public string? CalibrationEdfaPaCurrent { get; set; }
        public string? FiberDensity { get; set; }
        public string? WavelengthAverageCount { get; set; }
        public string? MultiWaveReverse { get; set; }
        public string? AutoRun { get; set; }
        public string? SpeedMode { get; set; }
        public string? LaserType { get; set; }
        public string? AlgorithmType { get; set; }
        public string? WavelengthPrecisionMode { get; set; }
        public string? CenterWavelengths { get; set; }
        public string? CoefficientFilePath { get; set; }
        public string? ExternalCommPort { get; set; }
        public string? StorageInterval { get; set; }
        public string? DatabaseTableName { get; set; }
        public bool LocalStorageEnabled { get; set; }
        public int? SelectedMonitorChannel { get; set; }
        public Dictionary<int, string>? CenterWavelengthsByChannel { get; set; }
        public Dictionary<int, bool>? ChannelEnabledByChannel { get; set; }
        public Dictionary<int, string>? CoefficientFilePathsByChannel { get; set; }

        public bool EnableAlarmL1 { get; set; }
        public bool EnableDiffAlarm { get; set; }
        public string? ZoneChannel { get; set; }
        public string? TempCorrection { get; set; }
        public string? TemperatureAxisMin { get; set; }
        public string? TemperatureAxisMax { get; set; }
        public string? ZoneCount { get; set; }
        public string? ZoneLength { get; set; }
        public Dictionary<int, AlarmChannelStateSnapshot>? AlarmSettingsByChannel { get; set; }
        public List<ZoneParameterItem>? ZoneRows { get; set; }
    }

    private enum SensorInfoDisplayMode
    {
        Wavelength,
        Temperature,
        Strain
    }

    private sealed class LoadedCoefficientProfile
    {
        public string FilePath { get; set; } = string.Empty;
        public int[] SourceSensorIndexes { get; set; } = Array.Empty<int>();
        public int[] SensorPositionsRaw { get; set; } = Array.Empty<int>();
        public float[] DisplaySensorPositionsM { get; set; } = Array.Empty<float>();
        public float PositionScaleToMeters { get; set; } = 1.0f;
        public int[] SensorWaveIndexes { get; set; } = Array.Empty<int>();
        public float[] TempSensitivityPmPerC { get; set; } = Array.Empty<float>();
        public float[] StrainSensitivity { get; set; } = Array.Empty<float>();
        public float[] ReferenceTemperaturesC { get; set; } = Array.Empty<float>();
        public float[] ReferenceStrains { get; set; } = Array.Empty<float>();
        public float[] ReferenceWavelengthsNm { get; set; } = Array.Empty<float>();
        public float[] ReferenceStrainWavelengthsNm { get; set; } = Array.Empty<float>();

        public int[] HardwareSensorPositionsRaw { get; set; } = Array.Empty<int>();
        public int[] HardwareSensorWaveIndexes { get; set; } = Array.Empty<int>();
        public float[] HardwareTempSensitivityPmPerC { get; set; } = Array.Empty<float>();
        public float[] HardwareStrainSensitivity { get; set; } = Array.Empty<float>();
        public float[] HardwareReferenceTemperaturesC { get; set; } = Array.Empty<float>();
        public float[] HardwareReferenceStrains { get; set; } = Array.Empty<float>();
        public float[] HardwareReferenceWavelengthsNm { get; set; } = Array.Empty<float>();
        public float[] HardwareReferenceStrainWavelengthsNm { get; set; } = Array.Empty<float>();
    }
}
