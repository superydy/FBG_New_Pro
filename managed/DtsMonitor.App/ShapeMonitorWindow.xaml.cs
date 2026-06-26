using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LineShape = System.Windows.Shapes.Line;
using PolylineShape = System.Windows.Shapes.Polyline;
using RectangleShape = System.Windows.Shapes.Rectangle;
using DtsMonitor.App.Models;
using DtsMonitor.App.Services;

namespace DtsMonitor.App;

public partial class ShapeMonitorWindow : Window
{
    private readonly Func<SnapshotModel?> _getSnapshot;
    private readonly Func<int, ShapeSensingProfile?> _getProfile;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, float[]> _referenceTopByChannel = new();
    private readonly Dictionary<int, float[]> _referenceBottomByChannel = new();
    private ShapeReconstructionResult? _latestResult;
    private bool _isRunning = true;

    public ShapeMonitorWindow(Func<SnapshotModel?> getSnapshot, Func<int, ShapeSensingProfile?> getProfile)
    {
        InitializeComponent();
        _getSnapshot = getSnapshot;
        _getProfile = getProfile;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _refreshTimer.Tick += (_, _) => RefreshShape();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        RefreshShape();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }

    private void RunToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isRunning = !_isRunning;
        RunToggleButton.Content = _isRunning ? "停止" : "运行";
        RunToggleButton.Background = BrushFromHex(_isRunning ? "#FF4B13" : "#2F9E44");
        if (_isRunning)
        {
            RefreshShape();
        }
    }

    private void SaveReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        SnapshotModel? snapshot = _getSnapshot();
        if (snapshot is null || snapshot.SensorWavelengthsNm.Length < 4)
        {
            ReferenceStatusTextBlock.Text = "当前没有可保存的实时波长";
            ReferenceStatusTextBlock.Foreground = BrushFromHex("#C2410C");
            return;
        }

        ShapeReconstructionSettings settings = ReadSettings(snapshot.SensorWavelengthsNm.Length);
        int start = Math.Clamp(settings.StartIndex, 0, snapshot.SensorWavelengthsNm.Length - 1);
        int end = Math.Clamp(settings.EndIndex == int.MaxValue ? snapshot.SensorWavelengthsNm.Length - 1 : settings.EndIndex, start, snapshot.SensorWavelengthsNm.Length - 1);
        int availableCount = end - start + 1;
        int offset = settings.PairOffset > 0 ? settings.PairOffset : availableCount / 2;
        int pairCount = Math.Min(offset, availableCount - offset);
        if (pairCount < 2)
        {
            ReferenceStatusTextBlock.Text = "索引范围无法形成上下光栅配对";
            ReferenceStatusTextBlock.Foreground = BrushFromHex("#C2410C");
            return;
        }

        float[] top = new float[pairCount];
        float[] bottom = new float[pairCount];
        for (int i = 0; i < pairCount; i++)
        {
            top[i] = snapshot.SensorWavelengthsNm[start + i];
            bottom[i] = snapshot.SensorWavelengthsNm[start + offset + i];
        }

        _referenceTopByChannel[snapshot.Channel] = top;
        _referenceBottomByChannel[snapshot.Channel] = bottom;
        ReferenceStatusTextBlock.Text = $"已保存参考：{pairCount} 组";
        ReferenceStatusTextBlock.Foreground = BrushFromHex("#2F9E44");
        RefreshShape();
    }

    private void ClearReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        SnapshotModel? snapshot = _getSnapshot();
        if (snapshot is not null)
        {
            _referenceTopByChannel.Remove(snapshot.Channel);
            _referenceBottomByChannel.Remove(snapshot.Channel);
        }
        else
        {
            _referenceTopByChannel.Clear();
            _referenceBottomByChannel.Clear();
        }

        ReferenceStatusTextBlock.Text = "未保存参考";
        ReferenceStatusTextBlock.Foreground = BrushFromHex("#C2410C");
        RefreshShape();
    }

    private void RefreshShape()
    {
        SnapshotModel? snapshot = _getSnapshot();
        if (snapshot is null)
        {
            DetectionTextBlock.Text = "等待实时数据";
            PitTextBlock.Text = "--";
            DetailTextBlock.Text = "--";
            FooterTextBlock.Text = "时间: --  FBG数量: --";
            MetricTextBlock.Text = "最大挠度: --";
            return;
        }

        FooterTextBlock.Text = $"时间: {snapshot.Timestamp:HH:mm:ss.fff}  FBG数量: {snapshot.SensorWavelengthsNm.Length}";
        if (!_isRunning)
        {
            return;
        }

        ShapeReconstructionSettings settings = ReadSettings(snapshot.SensorWavelengthsNm.Length);
        ShapeSensingProfile? profile = _getProfile(snapshot.Channel);
        _referenceTopByChannel.TryGetValue(snapshot.Channel, out float[]? referenceTop);
        _referenceBottomByChannel.TryGetValue(snapshot.Channel, out float[]? referenceBottom);

        ShapeReconstructionResult result = ShapeReconstructionService.Reconstruct2D(
            snapshot,
            profile,
            settings,
            referenceTop,
            referenceBottom);

        _latestResult = result;
        int strongestPitIndex = -1;
        int pitCount = result.IsValid ? CountCurvaturePits(result, out strongestPitIndex) : 0;
        DetectionTextBlock.Text = result.IsValid ? $"检测到 {pitCount} 个坑位" : "未检测到有效形状";
        DetectionTextBlock.Foreground = BrushFromHex(result.IsValid ? "#2F9E44" : "#C2410C");
        PitTextBlock.Text = result.IsValid && pitCount > 0 ? $"坑位 {strongestPitIndex + 1}" : "--";
        DetailTextBlock.Text = result.IsValid
            ? $"配对 {result.PairCount} 组，最大挠度 {result.MaxDeflectionM * 100.0f:F3} cm"
            : result.StatusText;
        DetailTextBlock.Foreground = BrushFromHex(result.IsValid ? "#2F9E44" : "#C2410C");
        MetricTextBlock.Text = result.IsValid
            ? $"最大挠度: {result.MaxDeflectionM * 100.0f:F3} cm"
            : "最大挠度: --";

        DrawResult();
    }

    private static int CountCurvaturePits(ShapeReconstructionResult result, out int strongestPitIndex)
    {
        strongestPitIndex = -1;
        float[] curvature = result.Curvature;
        if (curvature.Length < 3)
        {
            return 0;
        }

        float maxAbs = curvature.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0f).Max();
        if (maxAbs <= 0.000001f)
        {
            return 0;
        }

        float threshold = maxAbs * 0.6f;
        int count = 0;
        float strongest = 0f;
        int lastPeak = -8;
        for (int i = 1; i < curvature.Length - 1; i++)
        {
            float value = Math.Abs(curvature[i]);
            if (!float.IsFinite(value) ||
                value < threshold ||
                value < Math.Abs(curvature[i - 1]) ||
                value < Math.Abs(curvature[i + 1]) ||
                i - lastPeak < 8)
            {
                continue;
            }

            if (value > strongest)
            {
                strongest = value;
                strongestPitIndex = count;
            }

            count++;
            lastPeak = i;
        }

        return count;
    }

    private ShapeReconstructionSettings ReadSettings(int wavelengthCount)
    {
        int defaultEnd = Math.Max(0, wavelengthCount - 1);
        int defaultOffset = Math.Max(1, wavelengthCount / 2);
        return new ShapeReconstructionSettings
        {
            StartIndex = ReadInt(StartIndexTextBox.Text, 0),
            EndIndex = string.IsNullOrWhiteSpace(EndIndexTextBox.Text) ? defaultEnd : ReadInt(EndIndexTextBox.Text, defaultEnd),
            PairOffset = string.IsNullOrWhiteSpace(PairOffsetTextBox.Text) ? defaultOffset : ReadInt(PairOffsetTextBox.Text, defaultOffset),
            GratingDistanceM = ReadFloat(GratingDistanceTextBox.Text, 0.003f),
            SmoothWindow = ReadInt(SmoothWindowTextBox.Text, 7),
            XAxisMaxM = ReadFloat(XAxisTextBox.Text, 20f),
            YAxisMaxM = ReadFloat(YAxisTextBox.Text, 0.1f),
            AutoScale = AutoScaleCheckBox.IsChecked == true
        };
    }

    private static int ReadInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static float ReadFloat(string? text, float fallback)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value
            : fallback;
    }

    private void ShapeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawResult();

    private void CurvatureCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawResult();

    private void DrawResult()
    {
        DrawShapeChart();
        DrawCurvatureChart();
    }

    private void DrawShapeChart()
    {
        ShapeCanvas.Children.Clear();
        if (_latestResult is null || !_latestResult.IsValid)
        {
            DrawEmptyText(ShapeCanvas, "等待形状数据");
            return;
        }

        float maxX = AutoScaleCheckBox.IsChecked == true
            ? Math.Max(1f, _latestResult.ShapeX.Where(float.IsFinite).DefaultIfEmpty(1f).Max())
            : Math.Max(1f, ReadFloat(XAxisTextBox.Text, 20f));
        float maxAbsY = AutoScaleCheckBox.IsChecked == true
            ? Math.Max(0.01f, _latestResult.ShapeY.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0.01f).Max() * 1.25f)
            : Math.Max(0.01f, ReadFloat(YAxisTextBox.Text, 0.1f));

        DrawAxes(ShapeCanvas, 0f, maxX, -maxAbsY, maxAbsY, "X", "Y");
        DrawPolyline(ShapeCanvas, _latestResult.ShapeX, _latestResult.ShapeY, 0f, maxX, -maxAbsY, maxAbsY, BrushFromHex("#1D4ED8"), 2.4);
    }

    private void DrawCurvatureChart()
    {
        CurvatureCanvas.Children.Clear();
        if (_latestResult is null || !_latestResult.IsValid)
        {
            DrawEmptyText(CurvatureCanvas, "等待曲率数据");
            return;
        }

        float[] x = _latestResult.ArcPositionsM;
        float[] y = _latestResult.Curvature;
        float maxX = x.Where(float.IsFinite).DefaultIfEmpty(1f).Max();
        float maxAbsY = Math.Max(0.0001f, y.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0.0001f).Max() * 1.25f);
        DrawAxes(CurvatureCanvas, 0f, Math.Max(1f, maxX), -maxAbsY, maxAbsY, "s", "k");
        DrawPolyline(CurvatureCanvas, x, y, 0f, Math.Max(1f, maxX), -maxAbsY, maxAbsY, BrushFromHex("#DC2626"), 2.0);
    }

    private static void DrawEmptyText(Canvas canvas, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#6B7280"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        canvas.Children.Add(tb);
        Canvas.SetLeft(tb, Math.Max(0, canvas.ActualWidth / 2 - 56));
        Canvas.SetTop(tb, Math.Max(0, canvas.ActualHeight / 2 - 12));
    }

    private static void DrawAxes(Canvas canvas, float minX, float maxX, float minY, float maxY, string xTitle, string yTitle)
    {
        const double left = 76;
        const double right = 28;
        const double top = 30;
        const double bottom = 58;
        double width = Math.Max(1, canvas.ActualWidth - left - right);
        double height = Math.Max(1, canvas.ActualHeight - top - bottom);

        canvas.Children.Add(new RectangleShape
        {
            Width = width,
            Height = height,
            Stroke = BrushFromHex("#2F343B"),
            StrokeThickness = 1.4,
            Fill = Brushes.Transparent
        });
        Canvas.SetLeft(canvas.Children[^1], left);
        Canvas.SetTop(canvas.Children[^1], top);

        for (int i = 0; i <= 4; i++)
        {
            double x = left + width * i / 4.0;
            double y = top + height * i / 4.0;
            canvas.Children.Add(new LineShape { X1 = x, Y1 = top, X2 = x, Y2 = top + height, Stroke = BrushFromHex("#D6D9DE"), StrokeThickness = 1 });
            canvas.Children.Add(new LineShape { X1 = left, Y1 = y, X2 = left + width, Y2 = y, Stroke = BrushFromHex("#D6D9DE"), StrokeThickness = 1 });
            AddText(canvas, (minX + (maxX - minX) * i / 4f).ToString("F1", CultureInfo.InvariantCulture), x - 14, top + height + 10, 14);
            AddText(canvas, (maxY - (maxY - minY) * i / 4f).ToString("G3", CultureInfo.InvariantCulture), 10, y - 9, 14);
        }

        AddText(canvas, xTitle, left + width / 2 - 6, top + height + 34, 15);
        AddText(canvas, yTitle, left - 46, top + height / 2 - 8, 15);
    }

    private static void DrawPolyline(Canvas canvas, float[] xs, float[] ys, float minX, float maxX, float minY, float maxY, Brush stroke, double thickness)
    {
        const double left = 76;
        const double right = 28;
        const double top = 30;
        const double bottom = 58;
        double width = Math.Max(1, canvas.ActualWidth - left - right);
        double height = Math.Max(1, canvas.ActualHeight - top - bottom);
        var polyline = new PolylineShape
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round
        };

        int count = Math.Min(xs.Length, ys.Length);
        for (int i = 0; i < count; i++)
        {
            if (!float.IsFinite(xs[i]) || !float.IsFinite(ys[i]))
            {
                continue;
            }

            double x = left + (xs[i] - minX) / Math.Max(0.000001, maxX - minX) * width;
            double y = top + (maxY - ys[i]) / Math.Max(0.000001, maxY - minY) * height;
            polyline.Points.Add(new Point(x, y));
        }

        canvas.Children.Add(polyline);
    }

    private static void AddText(Canvas canvas, string text, double x, double y, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#333842"),
            FontSize = fontSize
        };
        canvas.Children.Add(tb);
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }
}
