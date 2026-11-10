using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace pxdMediaManager.Viewer;

/// <summary>
/// Shows one picture and lets you zoom (mouse wheel, around the cursor), pan (drag), rotate, and jump between "fit" and
/// 100%. The picture is positioned with a single matrix: centre it, rotate, scale, then move it to the view's centre plus
/// the pan offset.
/// </summary>
public sealed class ImageStage : Panel
{
    private const double MinScale = 0.02;
    private const double MaxScale = 40;

    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        RenderTransformOrigin = RelativePoint.TopLeft,
    };

    private Bitmap? _bitmap;
    private double _scale = 1;
    private bool _fit = true;
    private Vector _pan;
    private int _quarterTurns;
    private bool _dragging;
    private Point _dragStart;
    private Vector _panStart;

    public ImageStage()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // needed to receive pointer events over empty areas
        Focusable = true;
        Children.Add(_image);
    }

    /// <summary>Raised when the zoom level or rotation changes, so the toolbar can show the new zoom.</summary>
    public event Action? ViewChanged;

    public bool HasImage => _bitmap is not null;

    public int ZoomPercent => (int)Math.Round(_scale * 100);

    public void SetImage(Bitmap? bitmap)
    {
        _bitmap = bitmap;
        _image.Source = bitmap;
        _quarterTurns = 0;
        _pan = default;
        _fit = true;
        if (bitmap is not null)
        {
            _image.Width = bitmap.PixelSize.Width;
            _image.Height = bitmap.PixelSize.Height;
        }

        Apply();
    }

    public void Fit()
    {
        _fit = true;
        _pan = default;
        Apply();
    }

    public void ActualSize(Point? around = null) => ZoomTo(1, around);

    public void ZoomBy(double factor, Point? around = null) => ZoomTo(_scale * factor, around);

    public void RotateRight()
    {
        if (_bitmap is null)
            return;
        _quarterTurns = (_quarterTurns + 1) % 4;
        _pan = default;
        // A rotated picture usually needs a different fit, so go back to fitting it.
        _fit = true;
        Apply();
    }

    private void ZoomTo(double target, Point? around)
    {
        if (_bitmap is null || Bounds.Width <= 0)
            return;

        var newScale = Math.Clamp(target, MinScale, MaxScale);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var anchor = (around ?? centre) - centre; // the point to keep still, relative to the view's centre
        var oldScale = _scale;
        // Keep the spot under the cursor where it is: scale the cursor→picture-centre offset by the zoom ratio.
        _pan = anchor - (anchor - _pan) * (newScale / oldScale);
        _scale = newScale;
        _fit = false;
        Apply();
    }

    private double FitScale()
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return 1;
        var (w, h) = RotatedSize();
        // Big pictures shrink to fit; small ones stay at their real size rather than being blown up.
        return Math.Min(1.0, Math.Min(Bounds.Width / w, Bounds.Height / h));
    }

    private (double W, double H) RotatedSize()
    {
        var w = (double)_bitmap!.PixelSize.Width;
        var h = (double)_bitmap.PixelSize.Height;
        return _quarterTurns % 2 == 0 ? (w, h) : (h, w);
    }

    private void Apply()
    {
        if (_bitmap is null)
        {
            _image.RenderTransform = null;
            ViewChanged?.Invoke();
            return;
        }

        if (_fit)
            _scale = FitScale();

        // Panning is limited to the picture's own edges; a picture smaller than the view stays centred.
        var (rw, rh) = RotatedSize();
        var maxX = Math.Max(0, (rw * _scale - Bounds.Width) / 2);
        var maxY = Math.Max(0, (rh * _scale - Bounds.Height) / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));

        var m = Matrix.CreateTranslation(-_bitmap.PixelSize.Width / 2.0, -_bitmap.PixelSize.Height / 2.0)
                * Matrix.CreateRotation(_quarterTurns * Math.PI / 2)
                * Matrix.CreateScale(_scale, _scale)
                * Matrix.CreateTranslation(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        _image.RenderTransform = new MatrixTransform(m);
        ViewChanged?.Invoke();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        // Bounds are updated after arrange; applying on the next tick uses the new size (and keeps "fit" fitting on resize).
        Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        return size;
    }

    // ---- Mouse ---------------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bitmap is null || e.Delta.Y == 0)
            return;
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_bitmap is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            // Double-click: jump between fitting the picture and looking at it at full size.
            if (_fit || Math.Abs(_scale - 1) > 0.01)
                ActualSize(e.GetPosition(this));
            else
                Fit();
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _panStart = _pan;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        _pan = _panStart + (e.GetPosition(this) - _dragStart);
        Apply();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        Cursor = Cursor.Default;
        e.Pointer.Capture(null);
    }
}
