using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TtsStudio.Core.Engine;

namespace TtsStudio.App.Controls;

/// <summary>
/// Timeline of the last render: waveform envelope, the model's predicted pitch (dashed) and the
/// pitch after edits (solid), word boundaries and labels, the selected region's time span and the
/// playhead. Clicking seeks.
/// </summary>
public sealed class ProsodyView : FrameworkElement
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(SynthesisResult), typeof(ProsodyView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaybackTimeProperty = DependencyProperty.Register(
        nameof(PlaybackTime), typeof(double), typeof(ProsodyView), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightStartProperty = DependencyProperty.Register(
        nameof(HighlightStart), typeof(int), typeof(ProsodyView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightEndProperty = DependencyProperty.Register(
        nameof(HighlightEnd), typeof(int), typeof(ProsodyView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(ProsodyView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(ProsodyView));

    public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(
        nameof(IsStale), typeof(bool), typeof(ProsodyView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public SynthesisResult? Result { get => (SynthesisResult?)GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public double PlaybackTime { get => (double)GetValue(PlaybackTimeProperty); set => SetValue(PlaybackTimeProperty, value); }
    public int HighlightStart { get => (int)GetValue(HighlightStartProperty); set => SetValue(HighlightStartProperty, value); }
    public int HighlightEnd { get => (int)GetValue(HighlightEndProperty); set => SetValue(HighlightEndProperty, value); }
    public Brush? HighlightBrush { get => (Brush?)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public bool IsStale { get => (bool)GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }

    private const double Left = 48, Right = 14, Top = 24, Bottom = 10;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush GridBrush = Frozen(Color.FromRgb(0x2A, 0x2C, 0x32));
    private static readonly Brush WaveBrush = Frozen(Color.FromArgb(0x55, 0x8C, 0x93, 0xA6));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0xA0, 0xA4, 0xAC));
    private static readonly Brush FaintBrush = Frozen(Color.FromRgb(0x6E, 0x73, 0x7C));
    private static readonly Brush GuessBrush = Frozen(Color.FromRgb(0xFF, 0x70, 0x70));
    private static readonly Brush EditedBand = Frozen(Color.FromArgb(0x16, 0xF5, 0xA6, 0x4C));
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1);
    private static readonly Pen WordPen = FrozenPen(Frozen(Color.FromRgb(0x30, 0x32, 0x39)), 1);
    private static readonly Pen PredictedPen = FrozenPen(Frozen(Color.FromRgb(0x82, 0x87, 0x92)), 1.3, DashStyles.Dash);
    private static readonly Pen FinalPen = FrozenPen(Frozen(Color.FromRgb(0x7B, 0x93, 0xFF)), 2.2);
    private static readonly Pen PlayheadPen = FrozenPen(Frozen(Colors.White), 1.5);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Brush b, double w, DashStyle? dash = null)
    {
        var p = new Pen(b, w) { DashStyle = dash ?? DashStyles.Solid, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    public ProsodyView()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(Left, Top, Math.Max(10, ActualWidth - Left - Right), Math.Max(10, ActualHeight - Top - Bottom));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var r = Result;
        if (r is null || r.Duration <= 0)
        {
            var msg = Text("Render the sample (F5) to see its pitch, timing and waveform here.", 13, FaintBrush);
            dc.DrawText(msg, new Point((ActualWidth - msg.Width) / 2, (ActualHeight - msg.Height) / 2));
            return;
        }
        if (IsStale) dc.PushOpacity(0.45);

        double X(double t) => area.Left + t / r.Duration * area.Width;

        // Pitch scale (log, so semitone edits look the same height anywhere).
        var voiced = r.F0Final.Concat(r.F0Predicted).Where(v => v is > 40).Select(v => v!.Value).ToList();
        double lo = voiced.Count > 0 ? Math.Max(50, voiced.Min() * 0.85) : 80;
        double hi = voiced.Count > 0 ? voiced.Max() * 1.12 : 400;
        double Y(double hz) => area.Bottom - (Math.Log(hz) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo)) * area.Height;

        // Horizontal grid with Hz labels.
        foreach (double hz in new[] { 75.0, 100, 150, 200, 300, 400, 600 })
        {
            if (hz <= lo || hz >= hi) continue;
            double y = Y(hz);
            dc.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
            var label = Text($"{hz:0} Hz", 10, FaintBrush);
            dc.DrawText(label, new Point(area.Left - label.Width - 6, y - label.Height / 2));
        }

        // Selected region's span.
        if (HighlightEnd > HighlightStart)
        {
            var hit = r.Tokens.Where(t => t.Start is not null && t.CharStart < HighlightEnd && t.CharEnd > HighlightStart).ToList();
            if (hit.Count > 0)
            {
                double a = X(hit.Min(t => t.Start!.Value)), b = X(hit.Max(t => t.End!.Value));
                dc.DrawRectangle(HighlightBrush ?? Brushes.Gray, null, new Rect(a, area.Top - Top + 2, Math.Max(2, b - a), area.Height + Top - 2));
            }
        }

        // Waveform envelope, mirrored about the middle.
        if (r.Envelope.Count > 1)
        {
            double mid = area.Top + area.Height / 2, scale = area.Height * 0.42 / Math.Max(0.05, r.Envelope.Max());
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double step = r.Duration / r.Envelope.Count;
                ctx.BeginFigure(new Point(X(0), mid), true, true);
                for (int i = 0; i < r.Envelope.Count; i++) ctx.LineTo(new Point(X(i * step), mid - r.Envelope[i] * scale), false, false);
                for (int i = r.Envelope.Count - 1; i >= 0; i--) ctx.LineTo(new Point(X(i * step), mid + r.Envelope[i] * scale), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(WaveBrush, null, g);
        }

        // Words: boundaries, labels, and a tint where edits apply.
        double lastLabelEnd = double.MinValue;
        foreach (var t in r.Tokens)
        {
            if (t.Start is not double s || t.End is not double e || t.Source == "punct") continue;
            double x1 = X(s), x2 = X(e);
            if (!string.IsNullOrEmpty(t.Controls)) dc.DrawRectangle(EditedBand, null, new Rect(x1, area.Top, Math.Max(1, x2 - x1), area.Height));
            dc.DrawLine(WordPen, new Point(x1, area.Top - 4), new Point(x1, area.Bottom));
            var label = Text(t.Text, 11, t.IsGuessed ? GuessBrush : LabelBrush);
            double lx = (x1 + x2) / 2 - label.Width / 2;
            if (lx < lastLabelEnd + 4) continue; // too crowded at this zoom; the designer shows every word
            dc.DrawText(label, new Point(lx, 4));
            lastLabelEnd = lx + label.Width;
        }

        DrawCurve(dc, r.F0Predicted, r.F0Rate, X, Y, PredictedPen);
        DrawCurve(dc, r.F0Final, r.F0Rate, X, Y, FinalPen);

        if (IsStale) dc.Pop();

        if (PlaybackTime >= 0)
        {
            double x = X(PlaybackTime);
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, ActualHeight));
        }
    }

    private static void DrawCurve(DrawingContext dc, List<double?> f0, double rate, Func<double, double> x, Func<double, double> y, Pen pen)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool open = false;
            for (int i = 0; i < f0.Count; i++)
            {
                if (f0[i] is not double hz || hz < 40)
                {
                    open = false;
                    continue;
                }
                var p = new Point(x(i / rate), y(hz));
                if (!open) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
                open = true;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Result is not { Duration: > 0 } r) return;
        double width = Math.Max(10, ActualWidth - Left - Right);
        double t = Math.Clamp((e.GetPosition(this).X - Left) / width * r.Duration, 0, r.Duration);
        if (SeekCommand?.CanExecute(t) == true) SeekCommand.Execute(t);
    }
}
