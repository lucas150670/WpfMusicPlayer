// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using WpfMusicPlayer.Helpers;
using WpfMusicPlayer.ViewModels;

namespace WpfMusicPlayer.Views;

public sealed class LyricsD2DControl : Grid
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.Register(
            nameof(VerticalOffset),
            typeof(double),
            typeof(LyricsD2DControl),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static readonly DependencyProperty ScrollBarStyleProperty =
        DependencyProperty.Register(
            nameof(ScrollBarStyle),
            typeof(Style),
            typeof(LyricsD2DControl),
            new PropertyMetadata(null, OnScrollBarStyleChanged));

    public static readonly DependencyProperty LineContextMenuProperty =
        DependencyProperty.Register(
            nameof(LineContextMenu),
            typeof(ContextMenu),
            typeof(LyricsD2DControl));

    private static readonly SKColor LyricNormal = new(0xDD, 0xDD, 0xDD, 0x88);
    private static readonly SKColor LyricHighlight = SKColors.White;
    private static readonly SKColor SecondaryNormal = new(0xDD, 0xDD, 0xDD, 0x66);
    private static readonly SKColor SecondaryHighlight = new(0xDD, 0xDD, 0xDD, 0xBB);
    private static readonly SKColor HoverFill = new(0xFF, 0xFF, 0xFF, 0x2A);

    // IgnorePixelScaling = true makes SKElement pre-scale the canvas by the system DPI
    // so all drawing happens in DIP coordinates (matching the layout math below) while
    // the backing bitmap stays at full device-pixel resolution. (Verified against the
    // SkiaSharp.Views.WPF source: with the default false the canvas uses raw device
    // pixels and no DPI scale is applied.)
    private readonly SKElement _skElement = new()
    {
        IgnorePixelScaling = true
    };

    private readonly ScrollBar _scrollBar = new()
    {
        Orientation = Orientation.Vertical,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Stretch,
        Width = 8,
        Minimum = 0,
        ViewportSize = 1
    };

    private LyricsSkiaRenderer? _renderer;
    private LyricsViewModel? _viewModel;
    private bool _renderHookActive;
    private bool _dirty = true;
    private bool _layoutDirty = true;
    private bool _updatingScrollBar;
    private bool _pointerDown;
    private bool _isDragging;
    private Point _pointerDownPosition;
    private double _offsetAtPointerDown;
    private int _pendingAnchorIndex = -1;
    private bool _autoScrollEnabled = true;
    private DateTime _lastUserScrollUtc = DateTime.MinValue;
    private int _hoverIndex = -1;
    private int _hoverPaintIndex = -1;
    private float _hoverAlpha;
    private long _hoverTick;

    private float[] _lineTops = [];
    private float[] _lineHeights = [];
    private float[] _textHeights = [];
    private float[] _translationHeights = [];
    private float[] _romanjiHeights = [];
    private LyricsLayoutEngine.LyricSizeMetrics[] _focusedMetrics = [];
    // Per-line extra vertical offsets (DIPs) for the staggered auto-follow scroll.
    // Each starts at the scroll delta and eases back to zero; rows below the active
    // line start moving later than rows above, so the list flows from top to bottom.
    private float[] _lineScrollOffsets = [];
    private float[] _staggerFromOffsets = [];
    private int _staggerAnchorIndex;
    private long _staggerStartTicks;
    private bool _staggerActive;
    // Per wrapped-line karaoke highlight widths (DIPs) for the currently highlighted line.
    // Recomputed only when the highlighted line's Progress/text/layout changes.
    private float[] _karaokeLineWidths = [];
    private int _karaokeLineIndex = -1;
    private double _karaokeProgress = -1;
    private string? _karaokeText;
    private float _karaokeLayoutWidth = -1f;
    private double _contentHeight;
    private float _layoutWidth;
    private float _cachedLayoutWidth;
    private bool _cachedShowTranslation;
    private bool _cachedShowRomanji;
    private bool _metricsCacheDirty = true;

    public LyricsD2DControl()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        _skElement.HorizontalAlignment = HorizontalAlignment.Stretch;
        _skElement.VerticalAlignment = VerticalAlignment.Stretch;
        _skElement.PaintSurface += OnSkElementPaintSurface;
        Children.Add(_skElement);
        Children.Add(_scrollBar);

        _scrollBar.ValueChanged += OnScrollBarValueChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
    }

    public double VerticalOffset
    {
        get => (double)GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public Style? ScrollBarStyle
    {
        get => (Style?)GetValue(ScrollBarStyleProperty);
        set => SetValue(ScrollBarStyleProperty, value);
    }

    public ContextMenu? LineContextMenu
    {
        get => (ContextMenu?)GetValue(LineContextMenuProperty);
        set => SetValue(LineContextMenuProperty, value);
    }

    /// <summary>
    /// Scrolls the given line to the upper anchor position. The name is kept for
    /// existing callers; the target is no longer the vertical centre of the viewport.
    /// </summary>
    public void ScrollLyricToCenter(int index)
    {
        if (!_autoScrollEnabled)
        {
            if (!LyricsLayoutEngine.ShouldResumeAutoFollow(
                    true, DateTime.UtcNow - _lastUserScrollUtc))
            {
                _pendingAnchorIndex = index;
                return;
            }

            _autoScrollEnabled = true;
        }

        _pendingAnchorIndex = index;
        EnsureLayout();
        if (_lineHeights.Length == 0 || index < 0 || index >= _lineHeights.Length)
            return;

        var target = LyricsLayoutEngine.ComputeAnchorOffset(
            _lineTops[index],
            ActualHeight,
            _contentHeight);
        StartStaggeredScrollTo(target, index);
        _pendingAnchorIndex = -1;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SuspendAutoScroll();
        VerticalOffset = LyricsLayoutEngine.ClampOffset(
            VerticalOffset - e.Delta,
            ActualHeight,
            _contentHeight);
        e.Handled = true;
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_pointerDown && IsMouseCaptured)
        {
            var dy = pos.Y - _pointerDownPosition.Y;
            if (!_isDragging && Math.Abs(dy) >= 6)
            {
                _isDragging = true;
                SuspendAutoScroll();
            }

            if (_isDragging)
            {
                VerticalOffset = LyricsLayoutEngine.ClampOffset(
                    _offsetAtPointerDown - dy,
                    ActualHeight,
                    _contentHeight);
            }
        }

        if (!_isDragging)
            UpdateHoverIndex(HitTest(pos.Y));

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!_isDragging)
            UpdateHoverIndex(-1);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pointerDown = true;
        _isDragging = false;
        _pointerDownPosition = e.GetPosition(this);
        _offsetAtPointerDown = VerticalOffset;
        CaptureMouse();
        Focus();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_pointerDown)
        {
            var dragged = _isDragging;
            _pointerDown = false;
            _isDragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();

            if (!dragged && _viewModel is not null)
            {
                var pos = e.GetPosition(this);
                if ((pos - _pointerDownPosition).Length < 6)
                {
                    var index = HitTest(pos.Y);
                    if (index >= 0 && index < _viewModel.Lyrics.Count)
                        _viewModel.SeekToLyric(_viewModel.Lyrics[index]);
                }
            }
        }

        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _pointerDown = false;
        _isDragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var index = HitTest(e.GetPosition(this).Y);
        if (index >= 0 && _viewModel is not null && index < _viewModel.Lyrics.Count && LineContextMenu is { } menu)
        {
            menu.DataContext = _viewModel.Lyrics[index];
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        }

        base.OnMouseRightButtonUp(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _metricsCacheDirty = true;
        _layoutDirty = true;
        _dirty = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as LyricsViewModel);
        _renderer ??= new LyricsSkiaRenderer();
        StartRendering();
        _layoutDirty = true;
        _dirty = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
        DetachViewModel();
        _renderer?.Dispose();
        _renderer = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _metricsCacheDirty = true;
        _layoutDirty = true;
        _dirty = true;
        if (_autoScrollEnabled && _pendingAnchorIndex >= 0)
            ScrollLyricToCenter(_pendingAnchorIndex);
        else
            VerticalOffset = LyricsLayoutEngine.ClampOffset(VerticalOffset, ActualHeight, _contentHeight);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as LyricsViewModel);
        _layoutDirty = true;
        _dirty = true;
    }

    private void AttachViewModel(LyricsViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm))
            return;

        DetachViewModel();
        _viewModel = vm;
        if (vm is null)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.Lyrics.CollectionChanged += OnLyricsCollectionChanged;
        foreach (var line in vm.Lyrics)
            line.PropertyChanged += OnLyricLinePropertyChanged;
        ResetLineState();
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Lyrics.CollectionChanged -= OnLyricsCollectionChanged;
        foreach (var line in _viewModel.Lyrics)
            line.PropertyChanged -= OnLyricLinePropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsViewModel.IsTranslationVisible)
            or nameof(LyricsViewModel.IsRomanjiVisible))
        {
            _metricsCacheDirty = true;
            _layoutDirty = true;
            _dirty = true;
            return;
        }

        if (e.PropertyName == nameof(LyricsViewModel.CurrentLyricIndex))
            _dirty = true;
    }

    private void OnLyricsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is LyricLineViewModel line)
                    line.PropertyChanged -= OnLyricLinePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is LyricLineViewModel line)
                    line.PropertyChanged += OnLyricLinePropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset && _viewModel is not null)
        {
            foreach (var line in _viewModel.Lyrics)
                line.PropertyChanged += OnLyricLinePropertyChanged;
        }

        ResetLineState();
        _metricsCacheDirty = true;
        _layoutDirty = true;
        _dirty = true;
    }

    private void OnLyricLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Karaoke progress or highlight changed; a repaint is enough because all
        // lines share one font size, so layout metrics never change here.
        if (e.PropertyName is nameof(LyricLineViewModel.Progress) or nameof(LyricLineViewModel.IsHighlighted))
            _dirty = true;
    }

    private void ResetLineState()
    {
        var count = _viewModel?.Lyrics.Count ?? 0;
        Array.Resize(ref _lineTops, count);
        Array.Resize(ref _lineHeights, count);
        Array.Resize(ref _textHeights, count);
        Array.Resize(ref _translationHeights, count);
        Array.Resize(ref _romanjiHeights, count);
        Array.Resize(ref _focusedMetrics, count);
        Array.Resize(ref _lineScrollOffsets, count);
        Array.Resize(ref _staggerFromOffsets, count);
        _metricsCacheDirty = true;
    }

    private void StartRendering()
    {
        if (_renderHookActive)
            return;
        _renderHookActive = true;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopRendering()
    {
        if (!_renderHookActive)
            return;
        _renderHookActive = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        try
        {
            var hoverAnimating = TickHover();
            var staggerAnimating = TickStagger();
            if (!_dirty && !hoverAnimating && !staggerAnimating)
                return;

            EnsureLayout();
            _skElement.InvalidateVisual();
            _dirty = hoverAnimating || staggerAnimating;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _dirty = true;
        }
    }

    private void EnsureLayout()
    {
        if (!_layoutDirty && !_metricsCacheDirty)
            return;

        var vm = _viewModel;
        var count = vm?.Lyrics.Count ?? 0;
        if (count != _lineHeights.Length)
            ResetLineState();

        var width = Math.Max(1f, (float)ActualWidth
            - LyricsLayoutEngine.ContentPaddingLeft
            - LyricsLayoutEngine.ContentPaddingRight);
        _layoutWidth = width;
        if (vm is null || _renderer is null || count == 0)
        {
            _layoutDirty = false;
            _metricsCacheDirty = count == 0;
            _contentHeight = 0;
            UpdateScrollBar();
            return;
        }

        var showTranslation = vm.IsTranslationVisible;
        var showRomanji = vm.IsRomanjiVisible;
        if (Math.Abs(width - _cachedLayoutWidth) > 0.5f
            || showTranslation != _cachedShowTranslation
            || showRomanji != _cachedShowRomanji)
        {
            _metricsCacheDirty = true;
        }

        if (_metricsCacheDirty)
            RebuildMetricsCache(vm, width, showTranslation, showRomanji);

        for (var i = 0; i < count; i++)
        {
            var focused = _focusedMetrics[i];
            _textHeights[i] = focused.TextHeight;
            _translationHeights[i] = focused.TranslationHeight;
            _romanjiHeights[i] = focused.RomanjiHeight;
            _lineHeights[i] = focused.LineHeight;
        }

        float y = 0;
        for (var i = 0; i < count; i++)
        {
            _lineTops[i] = y;
            y += _lineHeights[i];
        }

        _contentHeight = y;
        var clamped = LyricsLayoutEngine.ClampOffset(VerticalOffset, ActualHeight, _contentHeight);
        if (Math.Abs(clamped - VerticalOffset) > 0.5)
            VerticalOffset = clamped;
        UpdateScrollBar();
        _layoutDirty = false;
    }

    private void RebuildMetricsCache(
        LyricsViewModel vm,
        float width,
        bool showTranslation,
        bool showRomanji)
    {
        var count = vm.Lyrics.Count;
        if (_focusedMetrics.Length != count)
            Array.Resize(ref _focusedMetrics, count);

        for (var i = 0; i < count; i++)
        {
            _focusedMetrics[i] = MeasureLine(
                vm.Lyrics[i],
                showTranslation,
                showRomanji,
                width);
        }

        _cachedLayoutWidth = width;
        _cachedShowTranslation = showTranslation;
        _cachedShowRomanji = showRomanji;
        _metricsCacheDirty = false;
    }

    private LyricsLayoutEngine.LyricSizeMetrics MeasureLine(
        LyricLineViewModel line,
        bool showTranslation,
        bool showRomanji,
        float width)
    {
        if (_renderer is null)
            return default;

        var text = _renderer.MeasureText(
            line.Text, LyricsLayoutEngine.MainFontSize, true, width);
        var translation = showTranslation && line.HasTranslation
            ? _renderer.MeasureText(
                line.Translation ?? string.Empty, LyricsLayoutEngine.SecondaryFontSize, false, width)
            : (0f, 0f);
        var romanji = showRomanji && line.HasRomanji
            ? _renderer.MeasureText(
                line.Romanji ?? string.Empty, LyricsLayoutEngine.SecondaryFontSize, false, width)
            : (0f, 0f);
        return new LyricsLayoutEngine.LyricSizeMetrics(
            text.Height,
            translation.Item1,
            romanji.Item1,
            text.Width,
            translation.Item2,
            romanji.Item2);
    }

    private void OnSkElementPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        // WPF-initiated repaints (resize etc.) bypass the CompositionTarget.Rendering
        // tick, so the layout must be validated here as well.
        EnsureLayout();

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (_renderer is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var vm = _viewModel;
        if (vm is null)
            return;

        var offset = (float)VerticalOffset;
        var viewportHeight = (float)ActualHeight;
        var width = _layoutWidth;
        var textX = LyricsLayoutEngine.ContentPaddingLeft;
        var count = vm.Lyrics.Count;
        if (_hoverPaintIndex >= 0 && _hoverPaintIndex < count && _hoverAlpha > 0.01f)
        {
            var hoverTop = _lineTops[_hoverPaintIndex] - offset + LineScrollOffset(_hoverPaintIndex);
            DrawHoverBackground(canvas, hoverTop, _lineHeights[_hoverPaintIndex]);
        }

        for (var i = 0; i < count; i++)
        {
            var top = _lineTops[i] - offset + LineScrollOffset(i);
            var bottom = top + _lineHeights[i];
            if (bottom < 0 || top > viewportHeight)
                continue;

            var line = vm.Lyrics[i];
            var highlighted = line.IsHighlighted;
            var focused = i < _focusedMetrics.Length ? _focusedMetrics[i] : default;
            var y = top + LyricsLayoutEngine.ItemPaddingY;

            DrawMainLineText(canvas, i, line, highlighted, textX, y, width, focused.TextHeight);
            y += _textHeights[i];

            if (_translationHeights[i] > 0 && line.Translation is not null)
            {
                y += LyricsLayoutEngine.SecondaryLineGap;
                DrawLineText(
                    canvas,
                    line.Translation,
                    LyricsLayoutEngine.SecondaryFontSize,
                    false,
                    textX,
                    y,
                    width,
                    focused.TranslationHeight,
                    highlighted ? SecondaryHighlight : SecondaryNormal);
                y += _translationHeights[i];
            }

            if (_romanjiHeights[i] > 0 && line.Romanji is not null)
            {
                y += LyricsLayoutEngine.SecondaryLineGap;
                DrawLineText(
                    canvas,
                    line.Romanji,
                    LyricsLayoutEngine.SecondaryFontSize,
                    false,
                    textX,
                    y,
                    width,
                    focused.RomanjiHeight,
                    highlighted ? SecondaryHighlight : SecondaryNormal);
            }
        }
    }

    private float LineScrollOffset(int index) =>
        index < _lineScrollOffsets.Length ? _lineScrollOffsets[index] : 0f;

    private void DrawLineText(
        SKCanvas canvas,
        string text,
        float fontSize,
        bool bold,
        float x,
        float y,
        float width,
        float height,
        SKColor color)
    {
        if (_renderer is null)
            return;

        _renderer.DrawText(
            canvas,
            text,
            fontSize,
            bold,
            x,
            y,
            width,
            height,
            color);
    }

    /// <summary>
    /// Draws the primary lyric text. For a highlighted line that carries karaoke progress
    /// this replicates the original karaoke behaviour: the dim base text is painted first,
    /// then the sung portion of every wrapped line is over-painted in the highlight colour,
    /// clipped per wrapped line to the length reported by text caret hit-testing.
    /// </summary>
    private void DrawMainLineText(
        SKCanvas canvas,
        int index,
        LyricLineViewModel line,
        bool highlighted,
        float x,
        float y,
        float width,
        float height)
    {
        if (_renderer is null)
            return;

        if (!highlighted || !line.IsProgressEnabled)
        {
            DrawLineText(
                canvas,
                line.Text,
                LyricsLayoutEngine.MainFontSize,
                true,
                x,
                y,
                width,
                height,
                highlighted ? LyricHighlight : LyricNormal);
            return;
        }

        var progress = Math.Clamp(line.Progress, 0.0, 1.0);
        var lineWidths = GetKaraokeLineWidths(index, line, width, progress);

        _renderer.DrawKaraokeText(
            canvas,
            line.Text,
            LyricsLayoutEngine.MainFontSize,
            true,
            x,
            y,
            width,
            height,
            1f,
            LyricNormal,
            LyricHighlight,
            lineWidths);
    }

    private float[] GetKaraokeLineWidths(int index, LyricLineViewModel line, float width, double progress)
    {
        if (_renderer is null)
            return [];

        // Recompute only when the highlighted line, its text, the layout width, or the
        // progress changes. This keeps the expensive text hit-testing off the per-frame
        // path unless karaoke progress actually advances.
        if (_karaokeLineIndex == index
            && string.Equals(_karaokeText, line.Text, StringComparison.Ordinal)
            && Math.Abs(_karaokeProgress - progress) < 0.0005
            && Math.Abs(_karaokeLayoutWidth - width) < 0.5f
            && _karaokeLineWidths.Length > 0)
        {
            return _karaokeLineWidths;
        }

        _karaokeLineWidths = _renderer.ComputeKaraokeLineWidths(
            line.Text,
            LyricsLayoutEngine.MainFontSize,
            true,
            width,
            progress);
        _karaokeLineIndex = index;
        _karaokeText = line.Text;
        _karaokeProgress = progress;
        _karaokeLayoutWidth = width;
        return _karaokeLineWidths;
    }

    private void DrawHoverBackground(SKCanvas canvas, float top, float height)
    {
        if (_renderer is null || height <= 0)
            return;

        var color = HoverFill.WithAlpha((byte)Math.Clamp(HoverFill.Alpha * _hoverAlpha, 0f, 255f));
        _renderer.FillRoundedRectangle(
            canvas,
            8f,
            top,
            Math.Max(0f, (float)ActualWidth - 16f),
            height,
            8f,
            color);
    }

    private void UpdateHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        if (index >= 0)
            _hoverPaintIndex = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
        _dirty = true;
    }

    private bool TickHover()
    {
        var target = _hoverIndex >= 0 ? 1f : 0f;
        if (Math.Abs(_hoverAlpha - target) < 0.01f)
        {
            _hoverAlpha = target;
            _hoverTick = 0;
            if (target <= 0f)
                _hoverPaintIndex = _hoverIndex;
            return false;
        }

        var now = DateTime.UtcNow.Ticks;
        var dt = _hoverTick == 0 ? 1.0 / 60.0 : (now - _hoverTick) / (double)TimeSpan.TicksPerSecond;
        _hoverTick = now;
        if (dt > 0.1)
            dt = 1.0 / 60.0;
        var step = (float)(dt / LyricsLayoutEngine.HoverFadeSeconds);
        if (target > _hoverAlpha)
            _hoverAlpha = Math.Min(target, _hoverAlpha + step);
        else
            _hoverAlpha = Math.Max(target, _hoverAlpha - step);
        return true;
    }

    /// <summary>
    /// Advances the staggered auto-follow scroll. The global offset is already at its
    /// target; each line's extra offset eases back to zero with a start delay that grows
    /// for rows further below the active line. Returns true while any line is moving.
    /// </summary>
    private bool TickStagger()
    {
        if (!_staggerActive)
            return false;

        var elapsedMs = (DateTime.UtcNow.Ticks - _staggerStartTicks) / (double)TimeSpan.TicksPerMillisecond;
        var animating = false;
        for (var i = 0; i < _lineScrollOffsets.Length; i++)
        {
            var delayMs = LyricsLayoutEngine.StaggerDelayMilliseconds(i, _staggerAnchorIndex);
            var t = (float)((elapsedMs - delayMs) / LyricsLayoutEngine.ScrollAnimationMilliseconds);
            if (t >= 1f)
            {
                _lineScrollOffsets[i] = 0f;
                continue;
            }

            animating = true;
            if (t <= 0f)
                continue;

            _lineScrollOffsets[i] = _staggerFromOffsets[i] * (1f - LyricsLayoutEngine.EaseInOutCubic(t));
        }

        if (!animating)
        {
            _staggerActive = false;
            return false;
        }

        _dirty = true;
        return true;
    }

    private void SuspendAutoScroll()
    {
        _autoScrollEnabled = false;
        _lastUserScrollUtc = DateTime.UtcNow;
        CancelStagger();
    }

    private int HitTest(double viewY)
    {
        EnsureLayout();
        // During a stagger the visual position of a line differs from its layout
        // position, so hit testing accounts for the per-line scroll offsets too.
        var contentY = viewY + VerticalOffset;
        for (var i = 0; i < _lineTops.Length; i++)
        {
            var top = _lineTops[i] + LineScrollOffset(i);
            if (contentY >= top && contentY < top + _lineHeights[i])
                return i;
        }

        return -1;
    }

    private void StartStaggeredScrollTo(double target, int anchorIndex)
    {
        target = LyricsLayoutEngine.ClampOffset(target, ActualHeight, _contentHeight);
        var delta = target - VerticalOffset;
        VerticalOffset = target;
        if (Math.Abs(delta) < 0.5)
            return;

        // Keep every line at its current visual position, then let TickStagger ease the
        // offsets back to zero. Accumulating handles a stagger starting mid-flight.
        for (var i = 0; i < _lineScrollOffsets.Length; i++)
        {
            _lineScrollOffsets[i] += (float)delta;
            _staggerFromOffsets[i] = _lineScrollOffsets[i];
        }

        _staggerAnchorIndex = anchorIndex;
        _staggerStartTicks = DateTime.UtcNow.Ticks;
        _staggerActive = true;
        _dirty = true;
    }

    private void CancelStagger()
    {
        if (!_staggerActive)
            return;

        _staggerActive = false;
        Array.Clear(_lineScrollOffsets, 0, _lineScrollOffsets.Length);
    }

    private void UpdateScrollBar()
    {
        var viewport = Math.Max(1d, ActualHeight);
        var scrollable = Math.Max(0d, _contentHeight - ActualHeight);
        _updatingScrollBar = true;
        _scrollBar.Maximum = scrollable;
        _scrollBar.ViewportSize = viewport;
        _scrollBar.Value = VerticalOffset;
        _scrollBar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        _updatingScrollBar = false;
    }

    private void OnScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingScrollBar)
            return;
        SuspendAutoScroll();
        VerticalOffset = e.NewValue;
    }

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LyricsD2DControl)d;
        var clamped = LyricsLayoutEngine.ClampOffset((double)e.NewValue, control.ActualHeight, control._contentHeight);
        if (Math.Abs(clamped - (double)e.NewValue) > 0.01)
        {
            control.VerticalOffset = clamped;
            return;
        }

        control.UpdateScrollBar();
        control._dirty = true;
    }

    private static void OnScrollBarStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LyricsD2DControl)d)._scrollBar.Style = e.NewValue as Style;
    }
}
