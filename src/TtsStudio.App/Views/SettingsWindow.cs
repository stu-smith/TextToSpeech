using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using TtsStudio.App.Services;
using TtsStudio.Core.Engine;

namespace TtsStudio.App.Views;

/// <summary>App settings: the colour theme (applied immediately) and links to the app's folders.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings, RuntimeManager runtime)
    {
        _settings = settings;
        Title = "Settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = "Settings", Style = (Style)FindResource("TitleText") });

        // ---------------------------------------------------------------- appearance
        panel.Children.Add(new TextBlock { Text = "APPEARANCE", Style = (Style)FindResource("HeaderText"), Margin = new Thickness(0, 18, 0, 8) });
        var themes = new UniformGrid { Columns = 3 };
        foreach (var (theme, label) in new[] { (AppTheme.System, "System"), (AppTheme.Light, "Light"), (AppTheme.Dark, "Dark") })
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = "Theme",
                Style = (Style)FindResource("SegmentRadio"),
                IsChecked = settings.Theme == theme,
            };
            radio.Checked += (_, _) => SetTheme(theme);
            themes.Children.Add(radio);
        }
        var segment = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Child = themes };
        segment.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        panel.Children.Add(segment);
        panel.Children.Add(new TextBlock
        {
            Text = "System follows the app mode chosen in Windows settings (Personalisation › Colours).",
            Style = (Style)FindResource("DimText"),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // ---------------------------------------------------------------- folders
        panel.Children.Add(new TextBlock { Text = "FOLDERS", Style = (Style)FindResource("HeaderText"), Margin = new Thickness(0, 22, 0, 2) });
        AddFolder(panel, "Settings", AppSettings.FolderPath);
        if (!SamePath(runtime.Root, AppSettings.FolderPath))
            AddFolder(panel, "Speech runtime and voice models", runtime.Root);

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsDefault = true,
            IsCancel = true,
            Style = (Style)FindResource("AccentButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        Content = panel;
    }

    private void SetTheme(AppTheme theme)
    {
        _settings.Theme = theme;
        _settings.Save();
        ThemeManager.Apply(theme);
    }

    private void AddFolder(Panel panel, string label, string path)
    {
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("LabelText") });
        var link = new Hyperlink(new Run(path)) { ToolTip = "Open in File Explorer", TextDecorations = null };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.Click += (_, _) => OpenFolder(path);
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        panel.Children.Add(new TextBlock(link) { TextWrapping = TextWrapping.Wrap });
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Dialog.ShowError(this, "Could not open the folder", e.Message);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
