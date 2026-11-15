using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TtsStudio.App.Services;
using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Controls;

/// <summary>
/// The sample designer. Lays the text out word-wrapped, tints the characters each region
/// covers (overlaps blend), and draws every region as a bar in a lane under the line so that
/// overlapping regions stay distinguishable. Drag across text to select, click a bar or tinted
/// text to select a region (clicking again cycles through overlapping ones), drag the ends of
/// the selected region's bar to resize it. When render results match the text, each word's
/// phonemes are shown beneath it, guessed pronunciations are underlined, and the word being
/// spoken is highlighted during playback.
/// </summary>
public sealed class AnnotatedTextView : FrameworkElement
{
    // ------------------------------------------------------------------ dependency properties

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IList), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((AnnotatedTextView)d).OnRegionsChanged(e)));

    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion), typeof(Region), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(int), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(int), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TokensProperty = DependencyProperty.Register(
        nameof(Tokens), typeof(IList<TokenTiming>), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaybackTimeProperty = DependencyProperty.Register(
        nameof(PlaybackTime), typeof(double), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapToWordsProperty = DependencyProperty.Register(
        nameof(SnapToWords), typeof(bool), typeof(AnnotatedTextView), new PropertyMetadata(true));

    public static readonly DependencyProperty HoverInfoProperty = DependencyProperty.Register(
        nameof(HoverInfo), typeof(string), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(AnnotatedTextView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IList? Regions { get => (IList?)GetValue(RegionsProperty); set => SetValue(RegionsProperty, value); }
    public Region? SelectedRegion { get => (Region?)GetValue(SelectedRegionProperty); set => SetValue(SelectedRegionProperty, value); }
    public int SelectionStart { get => (int)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public int SelectionEnd { get => (int)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public IList<TokenTiming>? Tokens { get => (IList<TokenTiming>?)GetValue(TokensProperty); set => SetValue(TokensProperty, value); }
    public double PlaybackTime { get => (double)GetValue(PlaybackTimeProperty); set => SetValue(PlaybackTimeProperty, value); }
    public bool SnapToWords { get => (bool)GetValue(SnapToWordsProperty); set => SetValue(SnapToWordsProperty, value); }
    public string? HoverInfo { get => (string?)GetValue(HoverInfoProperty); set => SetValue(HoverInfoProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    // ------------------------------------------------------------------ metrics and resources

    private const double PadX = 28, PadY = 22, FontSize = 22, PhonemeFontSize = 12;
    private const double LaneHeight = 18, LaneGap = 3, LineGap = 14, HandleWidth = 7;
    private const double TextHeight = FontSize * 1.5;
    private double PhonemeRow => HasTokens ? 18 : 0;
    private double LaneTop => TextHeight + PhonemeRow + 4;
    private double LineHeight => LaneTop + _laneCount * (LaneHeight + LaneGap) + LineGap;

    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SmallFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Pen HandlePen = FrozenPen(Frozen(Color.FromRgb(0x16, 0x17, 0x1A)), 1);

    // Theme colours, reloaded from the palette when the theme changes.
    private Brush TextBrush = null!, DimBrush = null!, PhonemeBrush = null!, AccentBrush = null!, SelectionBrush = null!;
    private Brush PlayBrush = null!, LexiconBrush = null!, GuessBrush = null!;
    private Pen GuessPen = null!, SelectedPen = null!;

    private void LoadColours()
    {
        Brush Res(string key) => (Brush)FindResource(key);
        TextBrush = Res("TextBrush");
        DimBrush = Res("TextFaintBrush");
        PhonemeBrush = Res("PhonemeBrush");
        AccentBrush = Res("AccentBrush");
        SelectionBrush = Res("TextSelectionBrush");
        PlayBrush = Res("PlayHighlightBrush");
        LexiconBrush = Res("SuccessBrush");
        GuessBrush = Res("DangerBrush");
        GuessPen = FrozenPen(GuessBrush, 1.5, new DashStyle([2, 2], 0));
        SelectedPen = FrozenPen(Res("MarkerBrush"), 1.5);
    }

    private void OnThemeChanged()
    {
        LoadColours();
        InvalidateMeasure(); // the laid-out text carries its brush
        InvalidateVisual();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Brush b, double w, DashStyle? dash = null)
    {
        var p = new Pen(b, w) { DashStyle = dash ?? DashStyles.Solid };
        p.Freeze();
        return p;
    }

    // ------------------------------------------------------------------ layout state

    private readonly record struct CharBox(int Line, double X, double W);
    private sealed record Seg(int Start, FormattedText Ft, int Line, double X);
    private sealed record Bar(Rect Rect, Region Region, bool First, bool Last);

    private CharBox[] _chars = [new(0, 0, 0)];
    private readonly List<Seg> _segs = [];
    private int _lineCount = 1;
    private int _laneCount = 1;
    private Dictionary<Region, int> _lanes = [];
    private readonly List<Bar> _bars = [];
    private double _pixelsPerDip = 1;
    private string _text = ""; // the text the current layout was built for

    private bool HasTokens => Tokens is { Count: > 0 };

    private IEnumerable<Region> AllRegions => Regions?.OfType<Region>() ?? [];

    public AnnotatedTextView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        LoadColours();
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += OnThemeChanged;
            OnThemeChanged();
        };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    // ------------------------------------------------------------------ region tracking

    private void OnRegionsChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= OnRegionCollectionChanged;
        if (e.OldValue is IList oldL) foreach (var r in oldL.OfType<INotifyPropertyChanged>()) r.PropertyChanged -= OnRegionPropertyChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += OnRegionCollectionChanged;
        if (e.NewValue is IList newL) foreach (var r in newL.OfType<INotifyPropertyChanged>()) r.PropertyChanged += OnRegionPropertyChanged;
    }

    private void OnRegionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var r in e.OldItems?.OfType<INotifyPropertyChanged>() ?? []) r.PropertyChanged -= OnRegionPropertyChanged;
        foreach (var r in e.NewItems?.OfType<INotifyPropertyChanged>() ?? []) r.PropertyChanged += OnRegionPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is IList all)
            foreach (var r in all.OfType<INotifyPropertyChanged>()) { r.PropertyChanged -= OnRegionPropertyChanged; r.PropertyChanged += OnRegionPropertyChanged; }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Region.Start) or nameof(Region.End)) InvalidateMeasure();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ layout

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 800 : available.Width;
        Layout(width);
        return new Size(width, PadY * 2 + _lineCount * LineHeight);
    }


    private FormattedText Format(string s, Typeface face, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, _pixelsPerDip);

    private void Layout(double width)
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string text = _text = Text ?? "";
        double avail = Math.Max(100, width - PadX * 2);
        double spaceWidth = Format(" ", TextFace, FontSize, TextBrush).WidthIncludingTrailingWhitespace;

        _segs.Clear();
        _chars = new CharBox[text.Length + 1];
        double x = 0;
        int line = 0;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c == '\n')
            {
                _chars[i++] = new(line, x, 0);
                line++;
                x = 0;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                double w = c == '\t' ? spaceWidth * 4 : c == '\r' ? 0 : spaceWidth;
                _chars[i++] = new(line, x, w);
                x += w;
                continue;
            }
            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
            var ft = Format(text[i..j], TextFace, FontSize, TextBrush);
            double wordWidth = ft.WidthIncludingTrailingWhitespace;
            if (x > 0 && x + wordWidth > avail)
            {
                line++;
                x = 0;
            }
            for (int k = 0; k < j - i; k++)
            {
                var g = ft.BuildHighlightGeometry(new Point(0, 0), k, 1);
                double cx = g?.Bounds.Left ?? 0, cw = g?.Bounds.Width ?? 0;
                _chars[i + k] = new(line, x + cx, cw);
            }
            _segs.Add(new Seg(i, ft, line, x));
            x += wordWidth;
            i = j;
        }
        _chars[text.Length] = new(line, x, 0);
        _lineCount = line + 1;

        var regions = AllRegions.Where(r => r.End > r.Start && r.Start < text.Length + 1).ToList();
        _lanes = RegionTools.AssignLanes(regions);
        _laneCount = Math.Max(1, _lanes.Count == 0 ? 1 : _lanes.Values.Max() + 1);
    }

    private double LineTop(int line) => PadY + line * LineHeight;

    /// <summary>Per-line horizontal extents of a character range.</summary>
    private IEnumerable<(int Line, double X1, double X2)> Extents(int start, int end)
    {
        start = Math.Clamp(start, 0, _chars.Length - 1);
        end = Math.Clamp(end, start, _chars.Length - 1);
        int curLine = -1;
        double x1 = 0, x2 = 0;
        for (int i = start; i < end; i++)
        {
            var c = _chars[i];
            if (_text[i] == '\n') continue;
            if (c.Line != curLine)
            {
                if (curLine >= 0) yield return (curLine, x1, x2);
                curLine = c.Line;
                x1 = c.X;
                x2 = c.X + c.W;
            }
            else
            {
                x1 = Math.Min(x1, c.X);
                x2 = Math.Max(x2, c.X + c.W);
            }
        }
        if (curLine >= 0) yield return (curLine, x1, x2);
    }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere
        string text = _text;
        _bars.Clear();

        if (text.Length == 0)
        {
            var ph = Format(Placeholder ?? "", TextFace, 15, DimBrush);
            ph.MaxTextWidth = Math.Max(50, ActualWidth - PadX * 2);
            dc.DrawText(ph, new Point(PadX, PadY + 6));
            return;
        }

        var regions = AllRegions.Where(r => _lanes.ContainsKey(r)).ToList();
        var selected = SelectedRegion;

        // 1. Tints behind the text: each region adds a translucent layer, so overlaps read darker.
        foreach (var r in regions.OrderBy(r => r == selected))
        {
            var brush = RegionColors.Brush(r, r == selected ? (byte)0x5A : (byte)0x26);
            foreach (var (line, x1, x2) in Extents(r.Start, r.End))
                dc.DrawRoundedRectangle(brush, null, new Rect(PadX + x1 - 1, LineTop(line) + 3, x2 - x1 + 2, TextHeight - 4), 3, 3);
        }

        // 2. Text selection.
        int selA = Math.Min(SelectionStart, SelectionEnd), selB = Math.Max(SelectionStart, SelectionEnd);
        if (selB > selA)
            foreach (var (line, x1, x2) in Extents(selA, selB))
                dc.DrawRoundedRectangle(SelectionBrush, null, new Rect(PadX + x1 - 1, LineTop(line) + 2, x2 - x1 + 2, TextHeight - 2), 3, 3);

        // 3. Word being spoken.
        var tokens = Tokens;
        if (tokens is not null && PlaybackTime >= 0)
        {
            var now = tokens.FirstOrDefault(t => t.Start <= PlaybackTime && PlaybackTime < t.End && t.CharStart is not null && t.Source != "punct");
            if (now is not null)
                foreach (var (line, x1, x2) in Extents(now.CharStart!.Value, now.CharEnd ?? now.CharStart.Value))
                {
                    dc.DrawRoundedRectangle(PlayBrush, null, new Rect(PadX + x1 - 2, LineTop(line) + 2, x2 - x1 + 4, TextHeight - 2), 3, 3);
                    dc.DrawRectangle(AccentBrush, null, new Rect(PadX + x1, LineTop(line) + TextHeight - 2, x2 - x1, 2.5));
                }
        }

        // 4. Text.
        foreach (var seg in _segs)
            dc.DrawText(seg.Ft, new Point(PadX + seg.X, LineTop(seg.Line) + (TextHeight - seg.Ft.Height) / 2));

        // 5. Phonemes under each word, and guessed pronunciations underlined.
        if (tokens is not null)
        {
            foreach (var t in tokens)
            {
                if (t.CharStart is not int cs || t.CharEnd is not int ce || ce > text.Length || t.Source == "punct") continue;
                var ext = Extents(cs, ce).FirstOrDefault();
                if (ext.X2 <= ext.X1) continue;
                double top = LineTop(ext.Line);
                if (t.IsGuessed)
                    dc.DrawLine(GuessPen, new Point(PadX + ext.X1, top + TextHeight - 1), new Point(PadX + ext.X2, top + TextHeight - 1));
                if (string.IsNullOrEmpty(t.Phonemes)) continue;
                var brush = t.Source switch { "markup" => AccentBrush, "lexicon" => LexiconBrush, "guessed" => GuessBrush, _ => PhonemeBrush };
                var ft = Format(t.Phonemes, SmallFace, PhonemeFontSize, brush);
                double cx = PadX + (ext.X1 + ext.X2) / 2 - ft.Width / 2;
                dc.DrawText(ft, new Point(Math.Max(2, cx), top + TextHeight));
            }
        }

        // 6. Region bars in lanes, plus pause markers.
        foreach (var r in regions.OrderBy(r => r == selected))
        {
            var color = RegionColors.For(r);
            var extents = Extents(r.Start, r.End).ToList();
            for (int k = 0; k < extents.Count; k++)
            {
                var (line, x1, x2) = extents[k];
                double top = LineTop(line) + LaneTop + _lanes[r] * (LaneHeight + LaneGap);
                var rect = new Rect(PadX + x1, top, Math.Max(8, x2 - x1), LaneHeight);
                _bars.Add(new Bar(rect, r, k == 0, k == extents.Count - 1));
                bool isSel = r == selected;
                dc.DrawRoundedRectangle(RegionColors.Brush(r, isSel ? (byte)0x80 : (byte)0x48), isSel ? SelectedPen : null, rect, 4, 4);
                dc.DrawRoundedRectangle(RegionColors.Brush(r), null, new Rect(rect.X, rect.Y, 3, rect.Height), 1.5, 1.5);
                if (k == 0)
                {
                    // A short bar's label may run on into free space in its lane, up to the next bar.
                    double room = ActualWidth - PadX / 2 - rect.X;
                    foreach (var o in regions)
                    {
                        if (o == r || _lanes[o] != _lanes[r]) continue;
                        foreach (var (oLine, ox1, _) in Extents(o.Start, o.End))
                            if (oLine == line && PadX + ox1 >= rect.Right - 0.5) room = Math.Min(room, PadX + ox1 - rect.X - 4);
                    }
                    string label = string.IsNullOrWhiteSpace(r.Label) ? r.Summary : $"{r.Label} · {r.Summary}";
                    var ft = Format(label, LabelFace, 11, TextBrush);
                    ft.MaxTextWidth = Math.Max(1, Math.Max(rect.Width, room) - 10);
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ft, new Point(rect.X + 7, rect.Y + (LaneHeight - ft.Height) / 2));
                }
                if (isSel)
                {
                    if (k == 0) DrawHandle(dc, rect.Left, rect);
                    if (k == extents.Count - 1) DrawHandle(dc, rect.Right, rect);
                }
            }
            if (r.PauseBeforeMs > 0 && extents.Count > 0) DrawPause(dc, extents[0].Line, extents[0].X1 - 5, color);
            if (r.PauseAfterMs > 0 && extents.Count > 0) DrawPause(dc, extents[^1].Line, extents[^1].X2 + 3, color);
        }
    }

    private static void DrawHandle(DrawingContext dc, double x, Rect bar) =>
        dc.DrawRoundedRectangle(Brushes.White, HandlePen, new Rect(x - 3, bar.Y - 2, 6, bar.Height + 4), 2, 2);

    private void DrawPause(DrawingContext dc, int line, double x, Color color)
    {
        var b = new SolidColorBrush(color);
        double top = LineTop(line) + TextHeight * 0.25, h = TextHeight * 0.5;
        dc.DrawRoundedRectangle(b, null, new Rect(PadX + x - 1, top, 2, h), 1, 1);
        dc.DrawRoundedRectangle(b, null, new Rect(PadX + x + 2, top, 2, h), 1, 1);
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>The caret position (0..Length) nearest to a point.</summary>
    private int BoundaryAt(Point p)
    {
        int line = Math.Clamp((int)Math.Floor((p.Y - PadY) / LineHeight), 0, _lineCount - 1);
        double x = p.X - PadX;
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _chars.Length; i++)
        {
            var c = _chars[i];
            if (c.Line != line) continue;
            double d = Math.Abs(x - c.X);
            if (d < bestDist) { bestDist = d; best = i; }
            if (i < _text.Length && _text[i] != '\n')
            {
                d = Math.Abs(x - (c.X + c.W));
                if (d < bestDist) { bestDist = d; best = i + 1; }
            }
        }
        return best < 0 ? _text.Length : best;
    }

    /// <summary>The character under a point, or -1.</summary>
    private int CharAt(Point p)
    {
        int line = (int)Math.Floor((p.Y - PadY) / LineHeight);
        double yIn = p.Y - LineTop(line);
        if (line < 0 || line >= _lineCount || yIn > TextHeight + PhonemeRow) return -1;
        double x = p.X - PadX;
        for (int i = 0; i < _text.Length; i++)
        {
            var c = _chars[i];
            if (c.Line == line && x >= c.X && x < c.X + c.W) return i;
        }
        return -1;
    }

    private Bar? BarAt(Point p) => _bars.LastOrDefault(b => b.Rect.Contains(p)) ?? _bars.LastOrDefault(b => InflateX(b.Rect, HandleWidth).Contains(p) && b.Region == SelectedRegion);

    private static Rect InflateX(Rect r, double dx) => new(r.X - dx, r.Y, r.Width + 2 * dx, r.Height);

    private enum Drag { None, Select, ResizeStart, ResizeEnd }

    private Drag _drag;
    private int _anchor;
    private Point _down;

    private (Region Region, bool AtStart)? HandleAt(Point p)
    {
        if (SelectedRegion is not { } r) return null;
        foreach (var b in _bars.Where(b => b.Region == r))
        {
            if (b.First && Math.Abs(p.X - b.Rect.Left) <= HandleWidth && p.Y >= b.Rect.Top - 3 && p.Y <= b.Rect.Bottom + 3) return (r, true);
            if (b.Last && Math.Abs(p.X - b.Rect.Right) <= HandleWidth && p.Y >= b.Rect.Top - 3 && p.Y <= b.Rect.Bottom + 3) return (r, false);
        }
        return null;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _down = p;
        e.Handled = true;

        if (HandleAt(p) is { } h)
        {
            _drag = h.AtStart ? Drag.ResizeStart : Drag.ResizeEnd;
            CaptureMouse();
            return;
        }
        if (BarAt(p) is { } bar)
        {
            SelectedRegion = bar.Region;
            ClearSelection();
            return;
        }
        if (e.ClickCount == 2)
        {
            int at = Math.Max(0, Math.Min(BoundaryAt(p), _text.Length - 1));
            var (a, b) = RegionTools.SnapToWords(_text, at, Math.Min(at + 1, _text.Length));
            SetSelection(a, b);
            SelectedRegion = null;
            return;
        }
        _anchor = BoundaryAt(p);
        _drag = Drag.Select;
        SetSelection(_anchor, _anchor);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        bool snap = SnapToWords && (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        switch (_drag)
        {
            case Drag.Select:
                int cur = BoundaryAt(p);
                int a = Math.Min(_anchor, cur), b = Math.Max(_anchor, cur);
                if (snap && b > a) (a, b) = RegionTools.SnapToWords(_text, a, b);
                SetSelection(a, b);
                return;
            case Drag.ResizeStart or Drag.ResizeEnd when SelectedRegion is { } r:
                int at = BoundaryAt(new Point(p.X, Math.Clamp(p.Y, 0, ActualHeight)));
                if (snap) at = SnapBoundary(at, _drag == Drag.ResizeStart);
                if (_drag == Drag.ResizeStart) r.Start = Math.Clamp(at, 0, r.End - 1);
                else r.End = Math.Clamp(at, r.Start + 1, _text.Length);
                return;
        }

        // Hover feedback.
        if (HandleAt(p) is not null) Cursor = Cursors.SizeWE;
        else if (BarAt(p) is not null) Cursor = Cursors.Hand;
        else Cursor = Cursors.IBeam;
        HoverInfo = Describe(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.Select && SelectionStart == SelectionEnd && (e.GetPosition(this) - _down).Length < 4)
        {
            ClearSelection();
            SelectRegionAt(CharAt(e.GetPosition(this)));
        }
        else if (_drag == Drag.Select)
        {
            SelectedRegion = null;
        }
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (BarAt(p) is { } bar)
        {
            SelectedRegion = bar.Region;
            ClearSelection();
            return;
        }
        int at = CharAt(p);
        int selA = Math.Min(SelectionStart, SelectionEnd), selB = Math.Max(SelectionStart, SelectionEnd);
        if (at >= 0 && at >= selA && at < selB) return; // keep the selection the menu will act on
        if (at >= 0 && !char.IsWhiteSpace(_text[at]))
        {
            var (a, b) = RegionTools.SnapToWords(_text, at, at + 1);
            SetSelection(a, b);
            SelectedRegion = null;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverInfo = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && (SelectedRegion is not null || SelectionStart != SelectionEnd))
        {
            SelectedRegion = null;
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetSelection(0, _text.Length);
            SelectedRegion = null;
            e.Handled = true;
        }
    }

    private void SelectRegionAt(int index)
    {
        if (index < 0)
        {
            SelectedRegion = null;
            return;
        }
        // Innermost first; clicking the same spot again cycles through overlapping regions.
        var covering = AllRegions.Where(r => r.Start <= index && index < r.End).OrderBy(r => r.Length).ThenBy(r => r.Start).ToList();
        if (covering.Count == 0)
        {
            SelectedRegion = null;
            return;
        }
        int current = SelectedRegion is null ? -1 : covering.IndexOf(SelectedRegion);
        SelectedRegion = covering[(current + 1) % covering.Count];
    }

    private int SnapBoundary(int at, bool isStart)
    {
        string t = _text;
        if (isStart)
            while (at > 0 && at < t.Length && Markup.IsWordChar(t[at - 1]) && Markup.IsWordChar(t[at])) at--;
        else
            while (at > 0 && at < t.Length && Markup.IsWordChar(t[at - 1]) && Markup.IsWordChar(t[at])) at++;
        return at;
    }

    private void SetSelection(int a, int b)
    {
        SelectionStart = a;
        SelectionEnd = b;
    }

    private void ClearSelection() => SetSelection(0, 0);

    private string? Describe(Point p)
    {
        if (BarAt(p) is { } bar)
            return $"Region “{Excerpt(_text, bar.Region.Start, bar.Region.End)}”: {RegionTools.Title(bar.Region)}";
        int at = CharAt(p);
        if (at < 0) return null;
        var parts = new List<string>();
        var tok = Tokens?.FirstOrDefault(t => t.CharStart <= at && at < t.CharEnd);
        if (tok is not null)
        {
            string source = tok.Source switch
            {
                "markup" => "set by a region",
                "lexicon" => "from the project lexicon",
                "gold" or "silver" => "dictionary",
                "guessed" => "GUESSED: not in the dictionary, check it",
                _ => tok.Source,
            };
            parts.Add($"“{tok.Text}” /{tok.Phonemes}/ ({source})");
        }
        int n = AllRegions.Count(r => r.Start <= at && at < r.End);
        if (n > 0) parts.Add(n == 1 ? "1 region" : $"{n} overlapping regions (click again to cycle)");
        return parts.Count == 0 ? null : string.Join("   ·   ", parts);
    }

    private static string Excerpt(string t, int a, int b)
    {
        a = Math.Clamp(a, 0, t.Length);
        b = Math.Clamp(b, a, t.Length);
        string s = t[a..b];
        return s.Length > 40 ? s[..37] + "…" : s;
    }
}
