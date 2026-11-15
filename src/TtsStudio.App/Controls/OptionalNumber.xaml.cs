using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TtsStudio.App.Controls;

/// <summary>
/// Edits a <c>double?</c>: a checkbox says whether the property is set, a slider and a text box
/// change its value. Moving the slider or typing a value sets it; unticking clears it. With
/// <see cref="AllowUnset"/> false it edits a plain number.
/// </summary>
public partial class OptionalNumber : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(OptionalNumber),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((OptionalNumber)d).Sync()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(OptionalNumber), new PropertyMetadata("", (d, _) => ((OptionalNumber)d).Sync()));

    public static readonly DependencyProperty MinimumProperty = Number(nameof(Minimum), 0);
    public static readonly DependencyProperty MaximumProperty = Number(nameof(Maximum), 1);
    public static readonly DependencyProperty DefaultValueProperty = Number(nameof(DefaultValue), 0);
    public static readonly DependencyProperty StepProperty = Number(nameof(Step), 0.1);

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(OptionalNumber), new PropertyMetadata("", (d, _) => ((OptionalNumber)d).Sync()));

    public static readonly DependencyProperty ShowSignProperty = DependencyProperty.Register(
        nameof(ShowSign), typeof(bool), typeof(OptionalNumber), new PropertyMetadata(false, (d, _) => ((OptionalNumber)d).Sync()));

    public static readonly DependencyProperty AllowUnsetProperty = DependencyProperty.Register(
        nameof(AllowUnset), typeof(bool), typeof(OptionalNumber), new PropertyMetadata(true, (d, _) => ((OptionalNumber)d).Sync()));

    private static DependencyProperty Number(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(OptionalNumber), new PropertyMetadata(def, (d, _) => ((OptionalNumber)d).Sync()));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool ShowSign { get => (bool)GetValue(ShowSignProperty); set => SetValue(ShowSignProperty, value); }
    public bool AllowUnset { get => (bool)GetValue(AllowUnsetProperty); set => SetValue(AllowUnsetProperty, value); }

    private bool _syncing;

    public OptionalNumber()
    {
        InitializeComponent();
        Sync();
    }

    private void Sync()
    {
        if (SliderCtl is null) return;
        _syncing = true;
        try
        {
            Check.Content = Label;
            PlainLabel.Text = Label;
            Check.Visibility = AllowUnset ? Visibility.Visible : Visibility.Collapsed;
            PlainLabel.Visibility = AllowUnset ? Visibility.Collapsed : Visibility.Visible;
            SliderCtl.Margin = new Thickness(AllowUnset ? 24 : 0, 5, 0, 0);
            UnitText.Text = Unit;
            SliderCtl.Minimum = Minimum;
            SliderCtl.Maximum = Maximum;
            SliderCtl.SmallChange = Step;
            SliderCtl.LargeChange = Step * 5;
            SliderCtl.TickFrequency = Step;
            SliderCtl.IsSnapToTickEnabled = true;

            bool has = Value is not null;
            Check.IsChecked = has;
            SliderCtl.Value = Math.Clamp(Value ?? DefaultValue, Minimum, Maximum);
            SliderCtl.Opacity = has ? 1 : 0.45;
            if (!Box.IsKeyboardFocused) Box.Text = has ? Format(Value!.Value) : "";
            Box.Tag = has ? null : "unset";
        }
        finally
        {
            _syncing = false;
        }
    }

    private string Format(double v)
    {
        string s = v.ToString("0.##", CultureInfo.InvariantCulture);
        return ShowSign && v > 0 ? "+" + s : s;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e) => Value = Check.IsChecked == true ? DefaultValue : null;

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Value = Math.Round(Math.Round(e.NewValue / Step) * Step, 4);
    }

    private void OnBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e) => Box.SelectAll();

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            Box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Sync();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        string s = Box.Text.Trim().TrimStart('+');
        if (s.Length == 0)
        {
            if (AllowUnset) Value = null;
            Sync();
            return;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            Value = v;
        Sync();
    }
}
