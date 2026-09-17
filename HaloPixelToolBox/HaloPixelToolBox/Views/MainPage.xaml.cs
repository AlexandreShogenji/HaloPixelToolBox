using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using HaloPixelToolBox.Core.Models.Lighting;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace HaloPixelToolBox.Views;

public sealed partial class MainPage : Page
{
    private const double PreviewGeometryWidth = 1000;
    private const double PreviewGeometryHeight = 100;
    private const double PreviewGeometryPadding = 5;
    private const int RainbowGradientStopCount = 25;

    // Edit only these six normalized outer-frame points to match a revised physical
    // speaker outline: left tip, top-left, top-right, right tip, bottom-right,
    // bottom-left. The other three frames are generated as true parallel insets.
    private static readonly Point[] PreviewOuterFrameGeometry =
    [
        new(9.464, 38.659),
        new(73.238, 5),
        new(926.762, 5),
        new(990.536, 38.659),
        new(912.386, 95),
        new(87.614, 95)
    ];

    // Center-line insets in actual DIPs. Keep one ascending value per frame,
    // ordered outer-to-inner, and recalculate them when the stroke widths change.
    private static readonly double[] PreviewFrameInsets = [0, 10, 18, 24];

    private static readonly double[] PreviewGlowOpacityScales = [1, 0.82, 0.64, 0.5];

    private static readonly Color[] ColorTidePalette =
    [
        Color.FromArgb(255, 174, 52, 255),
        Color.FromArgb(255, 20, 48, 132),
        Color.FromArgb(255, 0, 100, 112),
        Color.FromArgb(255, 20, 108, 48),
        Color.FromArgb(255, 132, 116, 18),
        Color.FromArgb(255, 148, 72, 14),
        Color.FromArgb(255, 132, 22, 30),
        Color.FromArgb(255, 78, 20, 104)
    ];

    private static readonly Color[] RipplePalette =
    [
        Color.FromArgb(255, 174, 52, 255),
        Color.FromArgb(255, 40, 104, 255),
        Color.FromArgb(255, 255, 210, 34)
    ];

    private static readonly Color[] FlowLeftToRightPalette =
    [
        Color.FromArgb(255, 166, 52, 255),
        Color.FromArgb(255, 42, 108, 255),
        Color.FromArgb(255, 38, 218, 118),
        Color.FromArgb(255, 255, 220, 45)
    ];

    private static readonly Color[] FlowRightToLeftPalette =
    [
        Color.FromArgb(255, 255, 220, 45),
        Color.FromArgb(255, 255, 132, 32),
        Color.FromArgb(255, 246, 48, 62),
        Color.FromArgb(255, 48, 102, 255),
        Color.FromArgb(255, 170, 50, 255)
    ];

    private static readonly Color[] FlowCenterToRightPalette =
    [
        Color.FromArgb(255, 40, 220, 218),
        Color.FromArgb(255, 46, 116, 255),
        Color.FromArgb(255, 46, 224, 112),
        Color.FromArgb(255, 255, 222, 48)
    ];

    private static readonly Color[] FlowCenterToLeftPalette =
    [
        Color.FromArgb(255, 40, 220, 218),
        Color.FromArgb(255, 44, 116, 255),
        Color.FromArgb(255, 174, 52, 255)
    ];

    private readonly DispatcherTimer ambientPreviewAnimationTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(40)
    };
    private readonly Stopwatch ambientPreviewAnimationClock = new();
    private readonly SolidColorBrush solidFrameBrush = new(Colors.Transparent);
    private readonly List<PreviewGlowLayer> previewGlowLayers = [];
    private readonly List<GradientStop> rainbowGradientStops = [];
    private LinearGradientBrush? rainbowFrameBrush;
    private bool isPageLoaded;
    private bool isPreviewGlowAvailable = true;

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ambientPreviewAnimationTimer.Tick += AmbientPreviewAnimationTimer_Tick;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        isPageLoaded = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.StartMonitoring();
        UpdatePreviewFrameGeometry(DevicePreviewFrame.ActualWidth, DevicePreviewFrame.ActualHeight);
        DispatcherQueue.TryEnqueue(() =>
        {
            InitializePreviewGlowLayers();
            ConfigureAmbientPreviewAnimation();
        });
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        isPageLoaded = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ambientPreviewAnimationTimer.Stop();
        ambientPreviewAnimationClock.Reset();
        ViewModel.StopMonitoring();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.AmbientPreviewBrush)
            or nameof(MainPageViewModel.AmbientPreviewEnabled)
            or nameof(MainPageViewModel.AmbientPreviewEffect)
            or nameof(MainPageViewModel.AmbientPreviewBrightness)
            or nameof(MainPageViewModel.AmbientPreviewSpeed))
        {
            ConfigureAmbientPreviewAnimation();
        }
    }

    private void AmbientPreviewAnimationTimer_Tick(object? sender, object e)
        => RenderAmbientPreviewFrame(ambientPreviewAnimationClock.Elapsed);

    private void ConfigureAmbientPreviewAnimation()
    {
        if (!isPageLoaded || PreviewFrameOuter is null)
            return;

        ambientPreviewAnimationTimer.Stop();
        ambientPreviewAnimationClock.Restart();

        RenderAmbientPreviewFrame(TimeSpan.Zero);
        if (ViewModel.AmbientPreviewEnabled
            && ViewModel.AmbientPreviewEffect != AmbientLightEffect.Static)
        {
            ambientPreviewAnimationTimer.Start();
        }
        else
        {
            ambientPreviewAnimationClock.Stop();
        }
    }

    private void RenderAmbientPreviewFrame(TimeSpan elapsed)
    {
        if (!ViewModel.AmbientPreviewEnabled)
        {
            solidFrameBrush.Color = Colors.Transparent;
            ApplyFrameBrush(solidFrameBrush);
            UpdateGlowLayers(Colors.Transparent, 0, 0);
            return;
        }

        var brightnessIntensity = ViewModel.AmbientPreviewBrightness switch
        {
            AmbientLightBrightness.Low => 0.55,
            AmbientLightBrightness.Medium => 0.78,
            _ => 1.0
        };
        var glowOpacity = ViewModel.AmbientPreviewBrightness switch
        {
            AmbientLightBrightness.Low => 0.24,
            AmbientLightBrightness.Medium => 0.42,
            _ => 0.64
        };
        var glowRadius = ViewModel.AmbientPreviewBrightness switch
        {
            AmbientLightBrightness.Low => 7,
            AmbientLightBrightness.Medium => 11,
            _ => 16
        };

        if (ViewModel.AmbientPreviewEffect == AmbientLightEffect.Breathing)
        {
            var speed = Math.Clamp(ViewModel.AmbientPreviewSpeed, (byte)1, (byte)10);
            var cycleSeconds = 7.5 - ((speed - 1) * 0.5);
            var phase = elapsed.TotalSeconds / cycleSeconds * Math.Tau;
            var pulse = 0.3 + (0.7 * ((Math.Sin(phase - (Math.PI / 2)) + 1) / 2));
            var previewColor = ScaleColor(ViewModel.AmbientPreviewBrush.Color, brightnessIntensity * pulse);
            solidFrameBrush.Color = previewColor;
            ApplyFrameBrush(solidFrameBrush);
            UpdateGlowLayers(
                ViewModel.AmbientPreviewBrush.Color,
                glowOpacity * (0.25 + (0.75 * pulse)),
                glowRadius);
            return;
        }

        if (ViewModel.AmbientPreviewEffect == AmbientLightEffect.Static)
        {
            var previewColor = ScaleColor(ViewModel.AmbientPreviewBrush.Color, brightnessIntensity);
            solidFrameBrush.Color = previewColor;
            ApplyFrameBrush(solidFrameBrush);
            UpdateGlowLayers(ViewModel.AmbientPreviewBrush.Color, glowOpacity, glowRadius);
            return;
        }

        RenderRainbowFrame(elapsed, brightnessIntensity, glowOpacity, glowRadius);
    }

    private void RenderRainbowFrame(
        TimeSpan elapsed,
        double brightnessIntensity,
        double glowOpacity,
        double glowRadius)
    {
        EnsureRainbowFrameBrush();
        if (rainbowFrameBrush is null)
            return;

        var representativeColor = ViewModel.AmbientPreviewEffect switch
        {
            AmbientLightEffect.ColorTide => RenderUniformPaletteFrame(
                elapsed,
                ColorTidePalette,
                GetSpeedAdjustedDuration(20, 8),
                brightnessIntensity),
            AmbientLightEffect.Ripple => RenderUniformPaletteFrame(
                elapsed,
                RipplePalette,
                GetSpeedAdjustedDuration(14, 6),
                brightnessIntensity),
            AmbientLightEffect.Flow => RenderFlowFrame(
                elapsed,
                brightnessIntensity,
                includeCenterOutwardStage: true,
                trailLength: 0.68,
                slowStageSeconds: 5,
                fastStageSeconds: 3),
            AmbientLightEffect.Dynamic => RenderFlowFrame(
                elapsed,
                brightnessIntensity,
                includeCenterOutwardStage: false,
                trailLength: 0.32,
                slowStageSeconds: 6,
                fastStageSeconds: 4),
            _ => Colors.Transparent
        };

        ApplyFrameBrush(rainbowFrameBrush);
        UpdateGlowLayers(representativeColor, glowOpacity, glowRadius);
    }

    private Color RenderUniformPaletteFrame(
        TimeSpan elapsed,
        IReadOnlyList<Color> palette,
        double cycleSeconds,
        double brightnessIntensity)
    {
        var cycleProgress = Repeat(elapsed.TotalSeconds / cycleSeconds, 1);
        var previewColor = ScaleColor(
            InterpolateCyclicPalette(palette, cycleProgress),
            brightnessIntensity);

        foreach (var stop in rainbowGradientStops)
            stop.Color = previewColor;

        return previewColor;
    }

    private Color RenderFlowFrame(
        TimeSpan elapsed,
        double brightnessIntensity,
        bool includeCenterOutwardStage,
        double trailLength,
        double slowStageSeconds,
        double fastStageSeconds)
    {
        var stageCount = includeCenterOutwardStage ? 3 : 2;
        var stageSeconds = GetSpeedAdjustedDuration(slowStageSeconds, fastStageSeconds);
        var stagePosition = Repeat(elapsed.TotalSeconds / stageSeconds, stageCount);
        var stageIndex = (int)Math.Floor(stagePosition);
        var stageProgress = stagePosition - stageIndex;
        var residualColor = ScaleColor(
            ViewModel.AmbientPreviewBrush.Color,
            brightnessIntensity * 0.02);

        return stageIndex switch
        {
            0 => RenderDirectionalSweep(
                stageProgress,
                brightnessIntensity,
                trailLength,
                leftToRight: true,
                FlowLeftToRightPalette,
                residualColor),
            1 => RenderDirectionalSweep(
                stageProgress,
                brightnessIntensity,
                trailLength,
                leftToRight: false,
                FlowRightToLeftPalette,
                residualColor),
            _ => RenderCenterOutwardSweep(
                stageProgress,
                brightnessIntensity,
                trailLength,
                residualColor)
        };
    }

    private Color RenderDirectionalSweep(
        double progress,
        double brightnessIntensity,
        double trailLength,
        bool leftToRight,
        IReadOnlyList<Color> palette,
        Color residualColor)
    {
        // Let the head start and finish outside the visible span so each stage
        // fades fully to dark instead of jumping at its boundary.
        var travelDistance = 1 + trailLength + 0.08;
        var head = leftToRight
            ? -0.04 + (progress * travelDistance)
            : 1.04 - (progress * travelDistance);

        for (var index = 0; index < rainbowGradientStops.Count; index++)
        {
            var position = index / (double)(rainbowGradientStops.Count - 1);
            var distanceBehindHead = leftToRight
                ? head - position
                : position - head;
            var palettePosition = leftToRight
                ? position
                : 1 - position;
            rainbowGradientStops[index].Color = RenderSweepStop(
                distanceBehindHead,
                trailLength,
                palette,
                palettePosition,
                brightnessIntensity,
                residualColor);
        }

        return residualColor;
    }

    private Color RenderCenterOutwardSweep(
        double progress,
        double brightnessIntensity,
        double trailLength,
        Color residualColor)
    {
        var head = -0.04 + (progress * (1 + trailLength + 0.08));

        for (var index = 0; index < rainbowGradientStops.Count; index++)
        {
            var position = index / (double)(rainbowGradientStops.Count - 1);
            var radialPosition = Math.Abs(position - 0.5) * 2;
            var palette = position < 0.5
                ? FlowCenterToLeftPalette
                : FlowCenterToRightPalette;
            rainbowGradientStops[index].Color = RenderSweepStop(
                head - radialPosition,
                trailLength,
                palette,
                radialPosition,
                brightnessIntensity,
                residualColor);
        }

        return residualColor;
    }

    private static Color RenderSweepStop(
        double distanceBehindHead,
        double trailLength,
        IReadOnlyList<Color> palette,
        double palettePosition,
        double brightnessIntensity,
        Color residualColor)
    {
        if (distanceBehindHead < 0 || distanceBehindHead > trailLength)
            return residualColor;

        // Palette progress follows the head's travel path. A point therefore
        // keeps the color the head had when it passed and only loses brightness;
        // the trail no longer cycles through the palette by itself.
        var lifetime = 1 - (distanceBehindHead / trailLength);
        var intensity = Math.Pow(SmoothStep(lifetime), 0.58);
        var movingColor = ScaleColor(
            InterpolatePalette(palette, palettePosition),
            brightnessIntensity);
        return InterpolateColor(residualColor, movingColor, intensity);
    }

    private double GetSpeedAdjustedDuration(double slowSeconds, double fastSeconds)
    {
        var speed = Math.Clamp(ViewModel.AmbientPreviewSpeed, (byte)1, (byte)10);
        var normalizedSpeed = (speed - 1) / 9d;
        return slowSeconds + ((fastSeconds - slowSeconds) * normalizedSpeed);
    }

    private static Color InterpolateCyclicPalette(IReadOnlyList<Color> palette, double progress)
    {
        var scaledPosition = Repeat(progress, 1) * palette.Count;
        var firstIndex = (int)Math.Floor(scaledPosition) % palette.Count;
        var secondIndex = (firstIndex + 1) % palette.Count;
        var blend = SmoothStep(scaledPosition - Math.Floor(scaledPosition));
        return InterpolateColor(palette[firstIndex], palette[secondIndex], blend);
    }

    private static Color InterpolatePalette(IReadOnlyList<Color> palette, double progress)
    {
        if (palette.Count == 1)
            return palette[0];

        var scaledPosition = Math.Clamp(progress, 0, 1) * (palette.Count - 1);
        var firstIndex = Math.Min((int)Math.Floor(scaledPosition), palette.Count - 2);
        var blend = SmoothStep(scaledPosition - firstIndex);
        return InterpolateColor(palette[firstIndex], palette[firstIndex + 1], blend);
    }

    private static Color InterpolateColor(Color first, Color second, double blend)
    {
        var clampedBlend = Math.Clamp(blend, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(first.A + ((second.A - first.A) * clampedBlend)),
            (byte)Math.Round(first.R + ((second.R - first.R) * clampedBlend)),
            (byte)Math.Round(first.G + ((second.G - first.G) * clampedBlend)),
            (byte)Math.Round(first.B + ((second.B - first.B) * clampedBlend)));
    }

    private static double SmoothStep(double value)
    {
        var clampedValue = Math.Clamp(value, 0, 1);
        return clampedValue * clampedValue * (3 - (2 * clampedValue));
    }

    private void EnsureRainbowFrameBrush()
    {
        if (rainbowFrameBrush is not null)
            return;

        rainbowFrameBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        for (var index = 0; index < RainbowGradientStopCount; index++)
        {
            var stop = new GradientStop
            {
                Offset = index / (double)(RainbowGradientStopCount - 1),
                Color = Colors.White
            };
            rainbowGradientStops.Add(stop);
            rainbowFrameBrush.GradientStops.Add(stop);
        }
    }

    private void ApplyFrameBrush(Brush brush)
    {
        foreach (var polygon in GetPreviewFramePolygons())
            polygon.Stroke = brush;
    }

    private void InitializePreviewGlowLayers()
    {
        if (!isPreviewGlowAvailable || previewGlowLayers.Count > 0 || DevicePreviewFrame is null)
            return;

        DropShadow? pendingShadow = null;
        SpriteVisual? pendingVisual = null;
        Polygon? pendingPolygon = null;
        var pendingVisualAttached = false;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(DevicePreviewFrame).Compositor;
            foreach (var polygon in GetPreviewFramePolygons())
            {
                pendingPolygon = polygon;
                pendingShadow = compositor.CreateDropShadow();
                pendingShadow.BlurRadius = 16;
                pendingShadow.Offset = Vector3.Zero;
                pendingShadow.Opacity = 0;
                pendingShadow.Mask = polygon.GetAlphaMask();

                pendingVisual = compositor.CreateSpriteVisual();
                pendingVisual.RelativeSizeAdjustment = Vector2.One;
                pendingVisual.Shadow = pendingShadow;
                ElementCompositionPreview.SetElementChildVisual(polygon, pendingVisual);
                pendingVisualAttached = true;

                previewGlowLayers.Add(new PreviewGlowLayer(polygon, pendingShadow, pendingVisual));
                pendingShadow = null;
                pendingVisual = null;
                pendingPolygon = null;
                pendingVisualAttached = false;
            }
        }
        catch (Exception exception)
        {
            if (pendingVisualAttached && pendingPolygon is not null)
            {
                try
                {
                    ElementCompositionPreview.SetElementChildVisual(pendingPolygon, null);
                }
                catch (Exception cleanupException)
                {
                    Debug.WriteLine($"[MainPage] Failed to detach an incomplete preview glow visual: {cleanupException}");
                }
            }

            try
            {
                pendingVisual?.Dispose();
                pendingShadow?.Dispose();
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine($"[MainPage] Failed to dispose an incomplete preview glow visual: {cleanupException}");
            }

            DisablePreviewGlow(exception);
        }
    }

    private void RefreshPreviewGlowMasks()
    {
        if (!isPreviewGlowAvailable || previewGlowLayers.Count == 0)
            return;

        try
        {
            foreach (var glowLayer in previewGlowLayers)
                glowLayer.Shadow.Mask = glowLayer.Polygon.GetAlphaMask();
        }
        catch (Exception exception)
        {
            DisablePreviewGlow(exception);
        }
    }

    private void DisablePreviewGlow(Exception exception)
    {
        isPreviewGlowAvailable = false;
        Debug.WriteLine($"[MainPage] Composition preview glow was disabled; polygon stroke animation remains active. {exception}");

        foreach (var glowLayer in previewGlowLayers)
        {
            try
            {
                glowLayer.Shadow.Opacity = 0;
                glowLayer.Shadow.Mask = null;
                ElementCompositionPreview.SetElementChildVisual(glowLayer.Polygon, null);
                glowLayer.Visual.Dispose();
                glowLayer.Shadow.Dispose();
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine($"[MainPage] Failed to fully clean up a disabled preview glow layer: {cleanupException}");
            }
        }

        previewGlowLayers.Clear();
    }

    private void UpdateGlowLayers(Color color, double opacity, double radius)
    {
        if (!isPreviewGlowAvailable)
            return;

        for (var index = 0; index < previewGlowLayers.Count; index++)
            UpdateGlowLayer(index, color, opacity, radius);
    }

    private void UpdateGlowLayer(int index, Color color, double opacity, double radius)
    {
        if (!isPreviewGlowAvailable || index < 0 || index >= previewGlowLayers.Count)
            return;

        var shadow = previewGlowLayers[index].Shadow;
        shadow.Color = color;
        shadow.Opacity = (float)Math.Clamp(opacity * PreviewGlowOpacityScales[index], 0, 1);
        shadow.BlurRadius = (float)Math.Max(0, radius - (index * 1.5));
    }

    private Polygon[] GetPreviewFramePolygons()
        =>
        [
            PreviewFrameOuter,
            PreviewFrameMiddleOuter,
            PreviewFrameMiddleInner,
            PreviewFrameInner
        ];

    private static Color ScaleColor(Color color, double intensity)
    {
        var clampedIntensity = Math.Clamp(intensity, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * clampedIntensity),
            (byte)Math.Round(color.G * clampedIntensity),
            (byte)Math.Round(color.B * clampedIntensity));
    }

    private static double Repeat(double value, double length)
        => value - (Math.Floor(value / length) * length);

    private void DevicePreviewFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged can be raised while InitializeComponent is still assigning
        // later x:Name fields. Defer the calculation until the visual tree is ready.
        DispatcherQueue.TryEnqueue(() =>
            UpdatePreviewFrameGeometry(DevicePreviewFrame.ActualWidth, DevicePreviewFrame.ActualHeight));
    }

    private void UpdatePreviewFrameGeometry(double width, double height)
    {
        if (PreviewFrameOuter is null ||
            PreviewFrameMiddleOuter is null ||
            PreviewFrameMiddleInner is null ||
            PreviewFrameInner is null ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= PreviewGeometryPadding * 2 ||
            height <= PreviewGeometryPadding * 2)
            return;

        var polygons = new[]
        {
            PreviewFrameOuter,
            PreviewFrameMiddleOuter,
            PreviewFrameMiddleInner,
            PreviewFrameInner
        };

        var usableWidth = width - (PreviewGeometryPadding * 2);
        var usableHeight = height - (PreviewGeometryPadding * 2);
        var outerPoints = PreviewOuterFrameGeometry
            .Select(point => new Point(
                PreviewGeometryPadding + (point.X / PreviewGeometryWidth * usableWidth),
                PreviewGeometryPadding + (point.Y / PreviewGeometryHeight * usableHeight)))
            .ToArray();

        for (var frameIndex = 0; frameIndex < polygons.Length; frameIndex++)
        {
            var scaledPoints = new PointCollection();
            foreach (var point in CreateInsetPolygon(outerPoints, PreviewFrameInsets[frameIndex]))
                scaledPoints.Add(point);

            polygons[frameIndex].Points = scaledPoints;
        }

        RefreshPreviewGlowMasks();
    }

    private static Point[] CreateInsetPolygon(IReadOnlyList<Point> points, double inset)
    {
        if (inset == 0)
            return [.. points];

        var signedArea = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            signedArea += (points[index].X * next.Y) - (next.X * points[index].Y);
        }

        var orientation = signedArea >= 0 ? 1d : -1d;
        var shiftedStarts = new Point[points.Count];
        var edgeVectors = new Point[points.Count];

        for (var index = 0; index < points.Count; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var length = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
            if (length <= double.Epsilon)
                return [.. points];

            var inwardX = orientation * -edgeY / length;
            var inwardY = orientation * edgeX / length;
            shiftedStarts[index] = new Point(start.X + (inwardX * inset), start.Y + (inwardY * inset));
            edgeVectors[index] = new Point(edgeX, edgeY);
        }

        var insetPoints = new Point[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var previousEdge = (index - 1 + points.Count) % points.Count;
            insetPoints[index] = IntersectLines(
                shiftedStarts[previousEdge],
                edgeVectors[previousEdge],
                shiftedStarts[index],
                edgeVectors[index]);
        }

        return insetPoints;
    }

    private static Point IntersectLines(Point firstStart, Point firstVector, Point secondStart, Point secondVector)
    {
        var denominator = Cross(firstVector, secondVector);
        if (Math.Abs(denominator) <= double.Epsilon)
            return firstStart;

        var delta = new Point(secondStart.X - firstStart.X, secondStart.Y - firstStart.Y);
        var distanceAlongFirst = Cross(delta, secondVector) / denominator;
        return new Point(
            firstStart.X + (firstVector.X * distanceAlongFirst),
            firstStart.Y + (firstVector.Y * distanceAlongFirst));
    }

    private static double Cross(Point first, Point second)
        => (first.X * second.Y) - (first.Y * second.X);

    private sealed record PreviewGlowLayer(
        Polygon Polygon,
        DropShadow Shadow,
        SpriteVisual Visual);
}
