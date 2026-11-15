using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TtsStudio.App.Services;
using TtsStudio.App.ViewModels;
using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Views;

public partial class MainWindow : Window
{
    private static readonly (string Symbol, string Hint)[] PhonemeKeys =
    [
        ("ˈ", "primary stress (before the vowel)"), ("ˌ", "secondary stress"),
        ("A", "eɪ as in day"), ("I", "aɪ as in bite"), ("O", "oʊ as in go (US)"), ("Q", "əʊ as in go (UK)"),
        ("W", "aʊ as in now"), ("Y", "ɔɪ as in boy"), ("æ", "cat"), ("ɑ", "father"), ("ɔ", "thought"),
        ("ə", "about"), ("ɛ", "bed"), ("ɜ", "bird"), ("ɪ", "sit"), ("i", "see"), ("ʊ", "put"), ("u", "blue"),
        ("ʌ", "cup"), ("ŋ", "sing"), ("θ", "thin"), ("ð", "this"), ("ʃ", "she"), ("ʒ", "vision"),
        ("ʧ", "church"), ("ʤ", "judge"), ("ɹ", "red"), ("ɡ", "go"), ("T", "flap t, as in water (US)"), ("ᵊ", "weak schwa"),
    ];

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly EngineService _engine = new(new RuntimeManager());
    private Sample? _editing;
    private bool _syncingEditor;

    public MainWindow(AppSettings settings, string? startupPath)
    {
        _settings = settings;
        InitializeComponent();
        TitleBar.Apply(this);
        _vm = new MainViewModel(this, _engine, _settings);
        DataContext = _vm;

        if (_settings.Window is { } bounds) SourceInitialized += (_, _) => WindowPlacement.Set(this, bounds);
        if (_settings.PitchViewHeight is > 0 and var pitch) PitchRow.Height = new GridLength(pitch);

        BuildPhonemePalette();
        _vm.PropertyChanged += OnViewModelChanged;
        Designer.KeyDown += OnDesignerKeyDown;
        TextEditor.PreviewKeyDown += OnEditorKeyDown;
        AllowDrop = true;
        Drop += OnDrop;

        Loaded += async (_, _) =>
        {
            FitPitchView();
            _vm.Initialize(startupPath);
            SyncEditor();
            Designer.Focus();
            await _vm.StartEngineCommand.ExecuteAsync(null); // install / start in the background
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedSample):
            case nameof(MainViewModel.IsEditingText):
                SyncEditor();
                break;
        }
    }

    // ------------------------------------------------------------------ text editor

    /// <summary>Points the plain-text editor at the selected sample and keeps it in step with model changes (e.g. undo).</summary>
    private void SyncEditor()
    {
        if (_editing is not null) _editing.PropertyChanged -= OnEditingSampleChanged;
        _editing = _vm.SelectedSample;
        if (_editing is not null) _editing.PropertyChanged += OnEditingSampleChanged;
        SetEditorText(_editing?.Text ?? "");
        if (_vm.IsEditingText)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TextEditor.Focus();
                TextEditor.CaretIndex = TextEditor.Text.Length;
            });
        }
        else if (IsLoaded)
        {
            Designer.Focus();
        }
    }

    private void OnEditingSampleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Sample.Text) && _editing is not null && TextEditor.Text != _editing.Text) SetEditorText(_editing.Text);
    }

    private void SetEditorText(string text)
    {
        _syncingEditor = true;
        TextEditor.Text = text;
        _syncingEditor = false;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _editing is null || _editing.Text == TextEditor.Text) return;
        var removed = _editing.ReplaceText(TextEditor.Text);
        if (removed.Count > 0) _vm.Status = $"{removed.Count} region(s) removed with the deleted text (Ctrl+Z outside the editor to undo)";
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.IsEditingText = false;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ designer keys

    private void OnDesignerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && _vm.DeleteRegionCommand.CanExecute(null))
        {
            _vm.DeleteRegionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.IsEditingText == false && Keyboard.Modifiers == ModifierKeys.None && _vm.HasSelection)
        {
            _vm.AddRegionCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ phoneme palette

    private void BuildPhonemePalette()
    {
        foreach (var (symbol, hint) in PhonemeKeys)
        {
            var b = new Button
            {
                Content = symbol,
                ToolTip = hint,
                Style = (Style)FindResource("ChipButton"),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 4, 4),
                MinWidth = 26,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PhonemeFont"),
                FontSize = 13,
                Focusable = false,
            };
            b.Click += (_, _) => InsertPhoneme(symbol);
            PhonemePalette.Children.Add(b);
        }
    }

    private void InsertPhoneme(string symbol)
    {
        var box = PronunciationBox;
        int caret = box.IsKeyboardFocusWithin || box.SelectionStart > 0 ? box.SelectionStart : box.Text.Length;
        int length = box.IsKeyboardFocusWithin ? box.SelectionLength : 0;
        box.Text = box.Text.Remove(caret, length).Insert(caret, symbol);
        box.Focus();
        box.CaretIndex = caret + symbol.Length;
    }

    // ------------------------------------------------------------------ files and window

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
            files[0].EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(files[0]))
            _vm.OpenPath(files[0]);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_vm.ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        _settings.Window = WindowPlacement.Get(this) ?? _settings.Window;
        _settings.PitchViewHeight = PitchRow.ActualHeight;
        _vm.Shutdown();
    }

    /// <summary>Shrinks a restored pitch view height that would squeeze the designer below its minimum (e.g. on a smaller screen).</summary>
    private void FitPitchView()
    {
        double room = CentreGrid.ActualHeight - CentreGrid.RowDefinitions[0].MinHeight - CentreGrid.RowDefinitions[1].ActualHeight;
        if (room > 0 && PitchRow.ActualHeight > room) PitchRow.Height = new GridLength(Math.Max(PitchRow.MinHeight, room));
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _engine.DisposeAsync();
    }
}
