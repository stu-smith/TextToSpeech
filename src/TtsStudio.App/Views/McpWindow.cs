using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TtsStudio.App.Services;

namespace TtsStudio.App.Views;

/// <summary>
/// Connecting a coding agent to the MCP server: add it to Claude Code or Claude Desktop with one
/// click, or copy what to set up by hand for any other client.
/// </summary>
public sealed class McpWindow : Window
{
    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New");

    private readonly StackPanel _agents = new();

    /// <summary>The outcome of the last add or remove, shown under the agents until the next.</summary>
    private (string Text, bool IsError)? _message;

    public McpWindow()
    {
        Title = "Connect a coding agent (MCP)";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TitleBar.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        panel.Children.Add(new TextBlock { Text = "Connect a coding agent", Style = (Style)FindResource("TitleText") });
        panel.Children.Add(Dim(
            "Let an AI agent such as Claude Code or Claude Desktop create and edit TTS Studio projects, look up " +
            "pronunciations and render audio. The agent runs its own copy of the server in the background; projects " +
            "it saves update here automatically.", 8));

        // ---------------------------------------------------------------- one click
        panel.Children.Add(Header("ADD TO AN AGENT", 20));
        panel.Children.Add(_agents);
        BuildAgents();

        // ---------------------------------------------------------------- by hand
        panel.Children.Add(Header("SET IT UP BY HAND", 22));
        panel.Children.Add(Dim("Claude Code: run this in a terminal, then start a new session."));
        panel.Children.Add(CopyBox(AgentIntegration.ClaudeCodeCommand, "Copy command"));
        panel.Children.Add(Dim("Claude Desktop, or a project's .mcp.json: merge this into the config file.", 14));
        panel.Children.Add(CopyBox(AgentIntegration.Snippet, "Copy JSON", multiline: true));
        panel.Children.Add(Dim("Any other agent: add this command as a stdio MCP server.", 14));
        panel.Children.Add(CopyBox(AgentIntegration.CommandLine, "Copy command"));

        // ---------------------------------------------------------------- try it
        panel.Children.Add(Header("TRY IT", 22));
        var example = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Child = new TextBlock
            {
                Text = $"“Use {AgentIntegration.ServerName} to make a project with a short welcome message, and render it to welcome.wav.”",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
            },
        };
        example.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        panel.Children.Add(example);
        panel.Children.Add(Dim(
            "Agents start the server from where the app is now. If you move or update the app to another folder, " +
            "come back here and update each agent.", 10));

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 12, 24, 20),
        };
        close.Click += (_, _) => Close();

        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
    }

    /// <summary>A card per client this app can configure, rebuilt after every change from what the config files now say.</summary>
    private void BuildAgents()
    {
        _agents.Children.Clear();

        string? cli = AgentIntegration.FindClaudeCli();
        _agents.Children.Add(AgentCard(
            "Claude Code",
            AgentIntegration.ClaudeCodeState,
            AgentIntegration.ClaudeCodeConfigPath,
            unavailable: cli is null ? "The claude command was not found. Install Claude Code, or use the command below." : null,
            add: () => AgentIntegration.AddToClaudeCodeAsync(cli!),
            remove: () => AgentIntegration.RemoveFromClaudeCodeAsync(cli!),
            afterChange: "New Claude Code sessions will pick it up."));

        _agents.Children.Add(AgentCard(
            "Claude Desktop",
            AgentIntegration.ClaudeDesktopState,
            AgentIntegration.ClaudeDesktopConfigPath,
            unavailable: AgentIntegration.IsClaudeDesktopInstalled ? null : "Claude Desktop has not been set up on this account.",
            add: () => Task.FromResult(AgentIntegration.AddToClaudeDesktop()),
            remove: () => Task.FromResult(AgentIntegration.RemoveFromClaudeDesktop()),
            afterChange: "Quit and reopen Claude Desktop to pick it up."));

        if (_message is { } message)
        {
            var outcome = Dim(message.Text, 4);
            outcome.SetResourceReference(TextBlock.ForegroundProperty, message.IsError ? "DangerBrush" : "SuccessBrush");
            _agents.Children.Add(outcome);
        }
    }

    /// <param name="unavailable">Why the client cannot be changed from here, or null when it can.</param>
    private Border AgentCard(string name, McpRegistration registration, string configPath, string? unavailable,
        Func<Task<string?>> add, Func<Task<string?>> remove, string afterChange)
    {
        var (status, dot, action, adds) = registration.State switch
        {
            McpRegistrationState.Registered => ("Connected", "SuccessBrush", "Remove", false),
            McpRegistrationState.Elsewhere => ($"Set up for another copy of the app: {registration.Command ?? "unknown"}", "WarningBrush", "Update", true),
            McpRegistrationState.Unreadable => ($"Its settings file could not be read, so it is left alone: {configPath}", "DangerBrush", null, false),
            _ => ("Not connected", "TextFaintBrush", "Add", true),
        };
        if (unavailable is not null && registration.State == McpRegistrationState.NotRegistered)
            status = unavailable;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(2, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        light.SetResourceReference(Shape.FillProperty, dot);
        grid.Children.Add(light);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        var statusText = Dim(status, 2);
        statusText.FontSize = 12;
        text.Children.Add(statusText);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (action is not null)
        {
            var button = new Button
            {
                Content = action,
                MinWidth = 88,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = unavailable is null,
                ToolTip = unavailable ?? configPath,
            };
            ToolTipService.SetShowOnDisabled(button, true);
            if (adds) button.Style = (Style)FindResource("AccentButton");
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                button.Content = adds ? "Adding…" : "Removing…";
                string? error = await (adds ? add() : remove());
                _message = error is not null
                    ? (error, true)
                    : (adds ? $"Added to {name}. {afterChange}" : $"Removed from {name}. {afterChange}", false);
                BuildAgents();
            };
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel2Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    /// <summary>Read-only text to copy from, with a button that copies all of it.</summary>
    private static DockPanel CopyBox(string text, string label, bool multiline = false)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };

        // A fixed width, so every box and button lines up whatever the label.
        var copy = new Button { Content = label, Width = 120, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                copy.Content = "Copied ✓";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another app has the clipboard open; the text box can still be copied from.
                copy.Content = "Clipboard busy";
            }
        };
        copy.MouseLeave += (_, _) => copy.Content = label;
        DockPanel.SetDock(copy, Dock.Right);
        panel.Children.Add(copy);

        panel.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = Monospace,
            FontSize = 11.5,
            TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap,
            AcceptsReturn = multiline,
            HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private TextBlock Header(string text, double top) =>
        new() { Text = text, Style = (Style)FindResource("HeaderText"), Margin = new Thickness(0, top, 0, 8) };

    private TextBlock Dim(string text, double top = 0) =>
        new() { Text = text, Style = (Style)FindResource("DimText"), Margin = new Thickness(0, top, 0, 0) };
}
