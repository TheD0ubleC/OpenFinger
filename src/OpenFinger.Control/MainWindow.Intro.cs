using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private const double IntroLogoMaxWidth = 1120;
    private const double IntroLogoViewportRatio = 0.76;
    private const double IntroInitialVisibleDivisor = 5.0;
    private const double IntroInitialVisibleTrim = 30.0;
    private const double IntroVerticalOffset = -18.0;
    private static readonly TimeSpan IntroFadeInDelay = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan IntroFadeInDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan IntroRevealDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan IntroRevealDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan IntroPreMoveCropDuration = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan IntroMoveDuration = TimeSpan.FromMilliseconds(920);
    private static readonly TimeSpan IntroBackgroundFadeDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan IntroOverlayFadeDuration = TimeSpan.FromMilliseconds(240);
    private const double IntroBrandTargetNudgeX = 1;

    private bool _startupIntroStarted;
    private bool _startupIntroPlaying;

    private async Task BeginStartupIntroAsync(bool forceReplay = false)
    {
        if (_startupIntroPlaying || _startHiddenOnLaunch || !IsLoaded)
        {
            if (!forceReplay)
            {
                StartupIntroOverlay.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (!forceReplay && _startupIntroStarted)
        {
            StartupIntroOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        _startupIntroStarted = true;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(24);

        if (!IsLoaded || ActualWidth < 640 || ActualHeight < 420)
        {
            StartupIntroOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        await PlayStartupIntroCoreAsync();
    }

    private async Task PlayStartupIntroCoreAsync()
    {
        _startupIntroPlaying = true;

        if (BrandWordmarkViewport.ActualWidth <= 0 || BrandWordmarkViewport.ActualHeight <= 0)
        {
            UpdateLayout();
        }

        var fullWidth = Math.Min(ActualWidth * IntroLogoViewportRatio, IntroLogoMaxWidth);
        var logoRatio = ResolveIntroLogoRatio();
        var fullHeight = fullWidth * logoRatio;
        var startVisibleWidth = Math.Max(120, fullWidth / IntroInitialVisibleDivisor - IntroInitialVisibleTrim);
        var startTop = ((ActualHeight - fullHeight) / 2) + IntroVerticalOffset;
        var startLeft = (ActualWidth - startVisibleWidth) / 2;
        var revealEndLeft = (ActualWidth - fullWidth) / 2;
        var finalLeftCropRatio = ResolveFinalLeftCropRatio();
        var finalLeftCrop = fullWidth * finalLeftCropRatio;

        var previousOpacity = BrandWordmarkViewport.Opacity;
        BrandWordmarkViewport.Opacity = 0;

        StartupIntroOverlay.Visibility = Visibility.Visible;
        StartupIntroOverlay.Opacity = 1;
        StartupIntroBackground.Background =
            (TryFindResource("BrushBg") as Brush)?.CloneCurrentValue()
            ?? Background?.CloneCurrentValue()
            ?? Brushes.Transparent;

        StartupIntroLogoWrap.Width = fullWidth;
        StartupIntroLogoWrap.Height = fullHeight;
        StartupIntroLogoWrap.Opacity = 0;
        Canvas.SetLeft(StartupIntroLogoWrap, startLeft);
        Canvas.SetTop(StartupIntroLogoWrap, startTop);
        StartupIntroLogoScale.ScaleX = 1;
        StartupIntroLogoScale.ScaleY = 1;
        StartupIntroLogoTranslate.X = 0;
        StartupIntroLogoTranslate.Y = 0;

        SetIntroClip(fullHeight, 0, startVisibleWidth);
        UpdateRevealEdge(startVisibleWidth, 0, 1.0);

        try
        {
            await Task.Delay(IntroFadeInDelay);
            await AnimateAsync(IntroFadeInDuration, progress =>
            {
                StartupIntroLogoWrap.Opacity = progress;
            });

            await Task.Delay(IntroRevealDelay);
            await AnimateAsync(IntroRevealDuration, progress =>
            {
                var eased = SampleCubicBezier(progress, 0.76, 0, 0.24, 1);
                var currentVisible = Lerp(startVisibleWidth, fullWidth, eased);
                var left = (ActualWidth - currentVisible) / 2;
                Canvas.SetLeft(StartupIntroLogoWrap, left);
                SetIntroClip(fullHeight, 0, currentVisible);

                var pulse = 0.55 + 0.45 * Math.Sin(progress * Math.PI);
                var edgeOpacity = progress >= 1 ? 0 : 0.18 + (1 - progress) * 0.62 * pulse;
                var edgeScale = 0.9 + (1 - progress) * 0.12;
                UpdateRevealEdge(currentVisible, edgeOpacity, edgeScale);
            });

            Canvas.SetLeft(StartupIntroLogoWrap, revealEndLeft);
            SetIntroClip(fullHeight, 0, fullWidth);
            UpdateRevealEdge(fullWidth, 0, 1.0);

            var croppedStartLeft = revealEndLeft;
            if (finalLeftCrop > 1)
            {
                await AnimateAsync(IntroPreMoveCropDuration, progress =>
                {
                    var eased = SampleCubicBezier(progress, 0.76, 0, 0.24, 1);
                    var currentLeftCrop = Lerp(0, finalLeftCrop, eased);
                    var currentVisibleWidth = fullWidth - currentLeftCrop;
                    var currentLeft = (ActualWidth * 0.5) - (currentLeftCrop + (currentVisibleWidth * 0.5));
                    Canvas.SetLeft(StartupIntroLogoWrap, currentLeft);
                    SetIntroClip(fullHeight, currentLeftCrop, currentVisibleWidth);
                    var pulse = 0.65 + 0.35 * Math.Sin(progress * Math.PI);
                    UpdateRevealEdge(currentLeftCrop, 0.22 + ((1 - progress) * 0.42 * pulse), 0.96 + ((1 - progress) * 0.08));
                });

                croppedStartLeft = (ActualWidth * 0.5) - (finalLeftCrop + ((fullWidth - finalLeftCrop) * 0.5));
                Canvas.SetLeft(StartupIntroLogoWrap, croppedStartLeft);
                SetIntroClip(fullHeight, finalLeftCrop, fullWidth - finalLeftCrop);
                UpdateRevealEdge(finalLeftCrop, 0, 1.0);
            }

            var targetRect = GetElementRectRelativeToWindow(BrandWordmarkViewport);
            var visibleStartWidth = Math.Max(1, fullWidth - finalLeftCrop);
            if (targetRect.Width > 0 && targetRect.Height > 0)
            {
                var scale = targetRect.Width / visibleStartWidth;
                var deltaX = targetRect.Left - croppedStartLeft - (finalLeftCrop * scale) + IntroBrandTargetNudgeX;
                var deltaY = targetRect.Top - startTop;

                var showBrandTask = Task.Run(async () =>
                {
                    await Task.Delay((int)Math.Round(IntroMoveDuration.TotalMilliseconds * 0.76));
                    await Dispatcher.InvokeAsync(() => BrandWordmarkViewport.Opacity = 1, System.Windows.Threading.DispatcherPriority.Background);
                });

                await AnimateAsync(IntroMoveDuration, progress =>
                {
                    var eased = SampleCubicBezier(progress, 0.76, 0, 0.24, 1);
                    StartupIntroLogoTranslate.X = deltaX * eased;
                    StartupIntroLogoTranslate.Y = deltaY * eased;
                    var currentScale = Lerp(1, scale, eased);
                    StartupIntroLogoScale.ScaleX = currentScale;
                    StartupIntroLogoScale.ScaleY = currentScale;
                });

                await showBrandTask;
            }
            else
            {
                BrandWordmarkViewport.Opacity = 1;
            }

            await AnimateAsync(IntroBackgroundFadeDuration, progress =>
            {
                StartupIntroBackground.Opacity = 1 - progress;
            });

            await AnimateAsync(IntroOverlayFadeDuration, progress =>
            {
                StartupIntroOverlay.Opacity = 1 - progress;
            });
        }
        finally
        {
            StartupIntroLogoImage.Clip = null;
            StartupIntroOverlay.Visibility = Visibility.Collapsed;
            StartupIntroOverlay.Opacity = 1;
            StartupIntroBackground.Opacity = 1;
            StartupIntroLogoWrap.Opacity = 0;
            StartupIntroLogoScale.ScaleX = 1;
            StartupIntroLogoScale.ScaleY = 1;
            StartupIntroLogoTranslate.X = 0;
            StartupIntroLogoTranslate.Y = 0;
            UpdateRevealEdge(0, 0, 1.0);
            BrandWordmarkViewport.Opacity = previousOpacity <= 0 ? 1 : previousOpacity;
            _startupIntroPlaying = false;
        }
    }

    private double ResolveIntroLogoRatio()
    {
        if (StartupIntroLogoImage.Source is BitmapSource bitmap
            && bitmap.PixelWidth > 0
            && bitmap.PixelHeight > 0)
        {
            return bitmap.PixelHeight / (double)bitmap.PixelWidth;
        }

        return 1019d / 4688d;
    }

    private double ResolveFinalLeftCropRatio()
    {
        var imageWidth = BrandWordmarkImage.ActualWidth;
        if (imageWidth <= 0)
        {
            imageWidth = BrandWordmarkViewport.ActualHeight * (4688d / 1019d);
        }

        if (imageWidth <= 0)
        {
            return 0.195;
        }

        var leftCrop = Math.Max(0, -BrandWordmarkImage.Margin.Left);
        return Math.Clamp(leftCrop / imageWidth, 0, 0.5);
    }

    private void SetIntroClip(double fullHeight, double leftCrop, double visibleWidth)
    {
        var clipX = Math.Max(0, leftCrop);
        var clipWidth = Math.Max(0, visibleWidth);
        StartupIntroLogoImage.Clip = new RectangleGeometry(new Rect(clipX, 0, clipWidth, fullHeight));
    }

    private void UpdateRevealEdge(double visibleWidth, double opacity, double scaleY)
    {
        StartupIntroRevealEdge.Opacity = opacity;
        StartupIntroRevealEdgeScale.ScaleY = scaleY;
        StartupIntroRevealEdgeTranslate.X = visibleWidth - 18;
    }

    private Rect GetElementRectRelativeToWindow(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var point = element.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Rect(point.X, point.Y, element.ActualWidth, element.ActualHeight);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + ((end - start) * progress);
    }

    private static double SampleCubicBezier(double t, double p1x, double p1y, double p2x, double p2y)
    {
        var cx = 3 * p1x;
        var bx = (3 * (p2x - p1x)) - cx;
        var ax = 1 - cx - bx;
        var cy = 3 * p1y;
        var by = (3 * (p2y - p1y)) - cy;
        var ay = 1 - cy - by;

        static double Sample(double a, double b, double c, double u) => ((a * u + b) * u + c) * u;
        static double SampleDerivative(double a, double b, double c, double u) => (3 * a * u + 2 * b) * u + c;

        var x = Math.Clamp(t, 0, 1);
        var u = x;
        for (var i = 0; i < 6; i++)
        {
            var dx = SampleDerivative(ax, bx, cx, u);
            if (Math.Abs(dx) < 1e-6)
            {
                break;
            }

            var error = Sample(ax, bx, cx, u) - x;
            if (Math.Abs(error) < 1e-5)
            {
                break;
            }

            u -= error / dx;
        }

        u = Math.Clamp(u, 0, 1);
        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < 8; i++)
        {
            var mid = (low + high) * 0.5;
            var midX = Sample(ax, bx, cx, mid);
            if (Math.Abs(midX - x) < 1e-5)
            {
                u = mid;
                break;
            }

            if (midX < x)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }

            u = mid;
        }

        return Sample(ay, by, cy, u);
    }

    private async Task AnimateAsync(TimeSpan duration, Action<double> update)
    {
        if (duration <= TimeSpan.Zero)
        {
            update(1);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            update(progress);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        }

        update(1);
    }

    private async void OnBrandWordmarkPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3)
        {
            return;
        }

        e.Handled = true;
        await BeginStartupIntroAsync(forceReplay: true);
    }
}
