using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TtsStudio.App.Services;

namespace TtsStudio.App.Views;

/// <summary>Themed replacements for MessageBox: messages, choices, text prompts and copyable text.</summary>
public static class Dialog
{
    /// <summary>Shows a message with buttons; returns the index of the clicked button, or -1 if closed.</summary>
    public static int Show(Window? owner, string title, string message, params string[] buttons) =>
        Build(owner, title, message, null, buttons, 0);

    public static void ShowError(Window? owner, string title, string message) => Show(owner, title, message, "OK");

    /// <summary>Asks for text; returns null if cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string message, string initial = "", bool multiline = false)
    {
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 160 : double.NaN,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(0, 14, 0, 0),
        };
        int result = Build(owner, title, message, box, ["OK", "Cancel"], 0, focus: box);
        return result == 0 ? box.Text : null;
    }

    /// <summary>Shows read-only, selectable text (commands, logs) with a Copy button.</summary>
    public static void ShowText(Window? owner, string title, string message, string text, double height = 200)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = height,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 0),
        };
        while (true)
        {
            int r = Build(owner, title, message, box, ["Copy", "Close"], 1);
            if (r != 0) return;
            Clipboard.SetText(box.Text);
        }
    }

    private static int Build(Window? owner, string title, string message, FrameworkElement? extra, string[] buttons,
        int defaultButton, FrameworkElement? focus = null)
    {
        var app = Application.Current;
        var w = new Window
        {
            Title = title,
            Owner = owner is { IsLoaded: true } ? owner : null,
            WindowStartupLocation = owner is { IsLoaded: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            SizeToContent = SizeToContent.Height,
            Width = extra is TextBox { AcceptsReturn: true } or TextBox { IsReadOnly: true } ? 640 : 460,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            Background = (Brush)app.FindResource("PanelBrush"),
            Foreground = (Brush)app.FindResource("TextBrush"),
            FontFamily = (FontFamily)app.FindResource("UiFont"),
            FontSize = 13,
        };
        DarkTitleBar.Apply(w);
        int result = -1;

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)app.FindResource("TitleText"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)app.FindResource("TextDimBrush"), LineHeight = 19 });
        if (extra is not null)
        {
            if (extra.Parent is Panel old) old.Children.Remove(extra);
            panel.Children.Add(extra);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i],
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == defaultButton && extra is not TextBox { AcceptsReturn: true },
                IsCancel = i == buttons.Length - 1 && buttons.Length > 1,
            };
            if (i == defaultButton) b.Style = (Style)app.FindResource("AccentButton");
            b.Click += (_, _) =>
            {
                result = index;
                w.Close();
            };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.Content = panel;
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                result = defaultButton;
                w.Close();
            }
        };
        if (focus is not null) w.Loaded += (_, _) => { focus.Focus(); if (focus is TextBox tb) tb.SelectAll(); };
        w.ShowDialog();
        panel.Children.Remove(extra);
        return result;
    }
}
