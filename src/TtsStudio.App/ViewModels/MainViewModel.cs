using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TtsStudio.App.Services;
using TtsStudio.App.Views;
using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Core.Model;

namespace TtsStudio.App.ViewModels;

public sealed record VoiceOption(string Id, string Display);

public sealed partial class MainViewModel : ObservableObject
{
    private const string FileFilter = "TTS Studio project (*.ttsproj)|*.ttsproj|All files (*.*)|*.*";

    private readonly Window _window;
    private readonly ModelWatcher _watcher = new();
    private readonly UndoStack _undo = new();
    private readonly DispatcherTimer _snapshotTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<string, RenderEntry> _renders = [];
    private readonly string _renderDir = Path.Combine(Path.GetTempPath(), "TtsStudio", "render");
    private FileSystemWatcher? _fileWatcher;
    private string _savedSnapshot = "";
    private string? _diskText;
    private bool _restoring;
    private int _renderSeq;

    private sealed record RenderEntry(string Key, string Text, SynthesisResult Result);

    public MainViewModel(Window window, EngineService engine, AppSettings settings)
    {
        _window = window;
        Engine = engine;
        Settings = settings;
        AutoRender = settings.AutoRender;
        SnapToWords = settings.SnapToWords;
        _watcher.Changed += OnModelChanged;
        _snapshotTimer.Tick += (_, _) => FlushSnapshot();
        _renderTimer.Tick += async (_, _) =>
        {
            _renderTimer.Stop();
            if (SelectedSample is { } s) await RenderAsync(s, play: false, quiet: true);
        };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            CheckDiskChange();
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioPlayer.IsPlaying)) PlayCommand.NotifyCanExecuteChanged();
        };
        Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineService.State))
                _window.Dispatcher.BeginInvoke(() =>
                {
                    OnPropertyChanged(nameof(EngineBusy));
                    if (Engine.State == EngineState.Ready) ScheduleRender();
                });
        };
        Directory.CreateDirectory(_renderDir);
        CleanRenderCache();
        Project = new Project();
    }

    // ------------------------------------------------------------------ state

    public EngineService Engine { get; }
    public AudioPlayer Player { get; } = new();
    public AppSettings Settings { get; }
    public IReadOnlyList<VoiceInfo> VoiceList => Voices.All;
    public IReadOnlyList<string> Formats => Exporter.Formats;
    public IReadOnlyList<PresetInfo> PresetList => Presets.All;

    [ObservableProperty] public partial Project Project { get; private set; }
    [ObservableProperty] public partial string? ProjectPath { get; private set; }
    [ObservableProperty] public partial bool IsDirty { get; private set; }
    [ObservableProperty] public partial Sample? SelectedSample { get; set; }
    [ObservableProperty] public partial Region? SelectedRegion { get; set; }
    [ObservableProperty] public partial int SelectionStart { get; set; }
    [ObservableProperty] public partial int SelectionEnd { get; set; }
    [ObservableProperty] public partial SynthesisResult? Result { get; private set; }
    [ObservableProperty] public partial IList<TokenTiming>? Tokens { get; private set; }
    [ObservableProperty] public partial bool IsResultStale { get; private set; }
    [ObservableProperty] public partial bool IsRendering { get; private set; }
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string? HoverInfo { get; set; }
    [ObservableProperty] public partial bool AutoRender { get; set; }
    [ObservableProperty] public partial bool SnapToWords { get; set; }
    [ObservableProperty] public partial bool IsEditingText { get; set; }
    [ObservableProperty] public partial bool ShowProjectTab { get; set; }
    [ObservableProperty] public partial IReadOnlyList<VoiceOption> SampleVoiceOptions { get; private set; } = [];

    public string Title => $"{(IsDirty ? "● " : "")}{Project.Name} — TTS Studio";
    public string ProjectFileLabel => ProjectPath is null ? "Not saved yet" : ProjectPath;
    public bool HasSelection => SelectionEnd > SelectionStart && SelectedSample is not null;
    public string SelectionText => HasSelection ? Quote(SelectedSample!.Text[Math.Min(SelectionStart, SelectedSample.Text.Length)..Math.Min(SelectionEnd, SelectedSample.Text.Length)]) : "";
    public bool ShowRegionPanel => SelectedRegion is not null;
    public bool ShowSelectionPanel => SelectedRegion is null && HasSelection;
    public bool ShowSamplePanel => SelectedRegion is null && SelectedSample is not null;
    public bool EngineBusy => Engine.State is EngineState.Installing or EngineState.Starting;
    public string ResultSummary => Result is null ? "" :
        $"{Result.Duration:0.00} s of audio · rendered in {Result.SynthSeconds:0.00} s on CPU ({(Result.SynthSeconds > 0 ? Result.Duration / Result.SynthSeconds : 0):0.#}× real time)";

    private static string Quote(string s)
    {
        s = s.Replace('\n', ' ');
        return $"“{(s.Length > 60 ? s[..57] + "…" : s)}”";
    }

    partial void OnProjectChanged(Project value)
    {
        _watcher.Watch(value);
        UpdateVoiceOptions();
        OnPropertyChanged(nameof(Title));
    }

    partial void OnProjectPathChanged(string? value)
    {
        OnPropertyChanged(nameof(ProjectFileLabel));
        WatchFile(value);
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));

    partial void OnSelectedSampleChanged(Sample? value)
    {
        SelectedRegion = null;
        SelectionStart = SelectionEnd = 0;
        Player.Stop();
        IsEditingText = value is not null && string.IsNullOrEmpty(value.Text);
        ShowCachedResult();
        ScheduleRender();
        NotifyPanels();
        RenderCommandsChanged();
    }

    partial void OnSelectedRegionChanged(Region? value)
    {
        if (value is not null) SelectionStart = SelectionEnd = 0;
        NotifyPanels();
        DeleteRegionCommand.NotifyCanExecuteChanged();
        PlayRegionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectionStartChanged(int value) => NotifyPanels();
    partial void OnSelectionEndChanged(int value) => NotifyPanels();
    partial void OnAutoRenderChanged(bool value)
    {
        Settings.AutoRender = value;
        if (value) ScheduleRender();
    }

    partial void OnSnapToWordsChanged(bool value) => Settings.SnapToWords = value;
    partial void OnResultChanged(SynthesisResult? value) => OnPropertyChanged(nameof(ResultSummary));

    private void NotifyPanels()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(ShowRegionPanel));
        OnPropertyChanged(nameof(ShowSelectionPanel));
        OnPropertyChanged(nameof(ShowSamplePanel));
        AddRegionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateVoiceOptions()
    {
        string def = Voices.Describe(Project.DefaultVoice).Name;
        SampleVoiceOptions = [new VoiceOption("", $"Project default ({def})"), .. Voices.All.Select(v => new VoiceOption(v.Id, v.Display))];
    }

    // ------------------------------------------------------------------ change tracking, undo

    private void OnModelChanged(object source, string? property)
    {
        if (_restoring) return;
        if (property is nameof(Region.Summary) or nameof(Region.Kind)) return;
        IsDirty = true;
        _snapshotTimer.Stop();
        _snapshotTimer.Start();
        if (source == Project && property == nameof(Project.Name)) OnPropertyChanged(nameof(Title));
        if (source == Project && property == nameof(Project.DefaultVoice)) UpdateVoiceOptions();
        if (SelectedSample is { } s)
        {
            UpdateResultValidity(s);
            if (source is Sample && source != s) return;
            ScheduleRender();
        }
        if (source == SelectedSample && property == nameof(Sample.Text)) NotifyPanels();
    }

    private void FlushSnapshot()
    {
        _snapshotTimer.Stop();
        string snap = ProjectFile.Serialize(Project);
        _undo.Record(snap);
        IsDirty = snap != _savedSnapshot;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undo.CanUndo || _snapshotTimer.IsEnabled;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_snapshotTimer.IsEnabled) FlushSnapshot();
        if (_undo.Undo() is { } snap) Restore(snap, "Undo");
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_undo.Redo() is { } snap) Restore(snap, "Redo");
    }

    private bool CanRedo() => _undo.CanRedo;

    /// <summary>Replaces the project with a snapshot, keeping the selected sample and region if they still exist.</summary>
    private void Restore(string json, string what)
    {
        string? sampleId = SelectedSample?.Id, regionId = SelectedRegion?.Id;
        bool editing = IsEditingText;
        _restoring = true;
        try
        {
            Project = ProjectFile.Parse(json);
            var sample = (sampleId is null ? null : Project.FindSample(sampleId)) ?? Project.Samples.FirstOrDefault();
            SelectedSample = sample;
            IsEditingText = editing && sample is not null;
            SelectedRegion = sample?.Regions.FirstOrDefault(r => r.Id == regionId);
        }
        finally
        {
            _restoring = false;
        }
        IsDirty = json != _savedSnapshot;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Status = what;
        ShowCachedResult();
        ScheduleRender();
    }

    // ------------------------------------------------------------------ project files

    public void Initialize(string? path)
    {
        if (path is not null && File.Exists(path) && TryLoad(path)) return;
        if (Settings.LastProject is { } last && File.Exists(last) && TryLoad(last)) return;
        SetProject(DemoProject.Create(), null);
        Status = "Welcome! This example project shows what regions can do; press F5 to hear a sample.";
    }

    private void SetProject(Project project, string? path)
    {
        _restoring = true;
        try
        {
            Player.Stop();
            _renders.Clear();
            Project = project;
            ProjectPath = path;
            SelectedSample = project.Samples.FirstOrDefault();
        }
        finally
        {
            _restoring = false;
        }
        _savedSnapshot = ProjectFile.Serialize(project);
        _undo.Reset(_savedSnapshot);
        IsDirty = false;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RenderCommandsChanged();
    }

    private bool TryLoad(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            var project = ProjectFile.Parse(text);
            SetProject(project, Path.GetFullPath(path));
            _diskText = text;
            Settings.LastProject = ProjectPath;
            Status = $"Opened {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            Dialog.ShowError(_window, "Could not open the project", $"{path}\n\n{e.Message}");
            return false;
        }
    }

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    public bool ConfirmDiscard()
    {
        if (_snapshotTimer.IsEnabled) FlushSnapshot();
        if (!IsDirty) return true;
        int r = Dialog.Show(_window, "Save changes?", $"Do you want to save the changes to {Project.Name}?", "Save", "Don't save", "Cancel");
        return r switch
        {
            0 => Save(),
            1 => true,
            _ => false,
        };
    }

    [RelayCommand]
    private void New()
    {
        if (!ConfirmDiscard()) return;
        var p = new Project();
        p.Samples.Add(new Sample { Name = "Sample 1" });
        SetProject(p, null);
        IsEditingText = true;
        Status = "New project";
    }

    [RelayCommand]
    private void Open()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = FileFilter, Title = "Open project" };
        if (dlg.ShowDialog(_window) == true) TryLoad(dlg.FileName);
    }

    public void OpenPath(string path)
    {
        if (ConfirmDiscard()) TryLoad(path);
    }

    [RelayCommand]
    private void SaveProject() => Save();

    [RelayCommand]
    private void SaveAs() => Save(forceDialog: true);

    public bool Save(bool forceDialog = false)
    {
        string? path = ProjectPath;
        if (path is null || forceDialog)
        {
            var dlg = new SaveFileDialog
            {
                Filter = FileFilter,
                Title = "Save project",
                FileName = Exporter.SafeFileName(Project.Name) + ProjectFile.Extension,
                DefaultExt = ProjectFile.Extension,
            };
            if (dlg.ShowDialog(_window) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            if (_snapshotTimer.IsEnabled) FlushSnapshot();
            if (ProjectPath is null && Project.Name == "Untitled") Project.Name = Path.GetFileNameWithoutExtension(path);
            ProjectFile.Save(Project, path);
            _diskText = File.ReadAllText(path);
            _savedSnapshot = ProjectFile.Serialize(Project);
            _undo.Record(_savedSnapshot);
            ProjectPath = Path.GetFullPath(path);
            IsDirty = false;
            Settings.LastProject = ProjectPath;
            Status = $"Saved {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialog.ShowError(_window, "Could not save the project", e.Message);
            return false;
        }
    }

    // External edits (e.g. a coding agent using the MCP server) are picked up automatically.
    private void WatchFile(string? path)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        if (path is null) return;
        _fileWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        void Poke(object? s, FileSystemEventArgs e) => _window.Dispatcher.BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
        _fileWatcher.Changed += Poke;
        _fileWatcher.Created += Poke;
        _fileWatcher.Renamed += (s, e) => Poke(s, e);
    }

    private void CheckDiskChange()
    {
        if (ProjectPath is null || !File.Exists(ProjectPath)) return;
        string text;
        try
        {
            text = File.ReadAllText(ProjectPath);
        }
        catch (IOException)
        {
            _reloadTimer.Start(); // still being written; try again shortly
            return;
        }
        if (text == _diskText) return;
        Project fresh;
        try
        {
            fresh = ProjectFile.Parse(text);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            Status = $"The project file changed on disk but could not be read: {e.Message}";
            return;
        }
        _diskText = text;
        if (_snapshotTimer.IsEnabled) FlushSnapshot();
        if (IsDirty && Dialog.Show(_window, "Project changed on disk",
                "Another program (perhaps a coding agent) changed this project file. Reload it and lose your unsaved changes?",
                "Reload", "Keep mine") != 0)
            return;
        string json = ProjectFile.Serialize(fresh);
        _savedSnapshot = json;
        _undo.Record(json);
        Restore(json, "Reloaded: the project file was changed by another program");
        IsDirty = false;
    }

    // ------------------------------------------------------------------ samples

    [RelayCommand]
    private void AddSample()
    {
        var s = new Sample { Name = $"Sample {Project.Samples.Count + 1}" };
        int at = SelectedSample is null ? Project.Samples.Count : Project.Samples.IndexOf(SelectedSample) + 1;
        Project.Samples.Insert(at, s);
        SelectedSample = s;
        IsEditingText = true;
    }

    [RelayCommand]
    private void ImportMarkup()
    {
        string? markup = Dialog.Prompt(_window, "New sample from markup",
            "Type or paste annotated text; it becomes a sample with regions you can refine.\n\n" + Markup.Reference.Trim(),
            "I didn't say *she* stole the money. Generate an [IOSS](/ˈIɑs/) return?", multiline: true);
        if (markup is null) return;
        try
        {
            var (text, regions) = Markup.Parse(markup);
            var s = new Sample { Name = $"Sample {Project.Samples.Count + 1}", Text = text };
            foreach (var r in regions) s.Regions.Add(r);
            int at = SelectedSample is null ? Project.Samples.Count : Project.Samples.IndexOf(SelectedSample) + 1;
            Project.Samples.Insert(at, s);
            SelectedSample = s;
        }
        catch (MarkupException e)
        {
            Dialog.ShowError(_window, "Markup error", e.Message);
        }
    }

    [RelayCommand]
    private void DuplicateSample()
    {
        if (SelectedSample is not { } src) return;
        var copy = JsonSerializer.Deserialize<Sample>(JsonSerializer.Serialize(src, ProjectFile.JsonOptions), ProjectFile.JsonOptions)!;
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = src.Name + " (copy)";
        if (copy.FileName is not null) copy.FileName += " (copy)";
        foreach (var r in copy.Regions) r.Id = Guid.NewGuid().ToString("N")[..8];
        Project.Samples.Insert(Project.Samples.IndexOf(src) + 1, copy);
        SelectedSample = copy;
    }

    [RelayCommand]
    private void DeleteSample()
    {
        if (SelectedSample is not { } s) return;
        int i = Project.Samples.IndexOf(s);
        Project.Samples.Remove(s);
        SelectedSample = Project.Samples.Count == 0 ? null : Project.Samples[Math.Min(i, Project.Samples.Count - 1)];
        Status = $"Deleted “{s.Name}” (Ctrl+Z to undo)";
    }

    [RelayCommand]
    private void MoveSample(string direction)
    {
        if (SelectedSample is not { } s) return;
        int i = Project.Samples.IndexOf(s), j = i + (direction == "up" ? -1 : 1);
        if (j < 0 || j >= Project.Samples.Count) return;
        Project.Samples.Move(i, j);
    }

    // ------------------------------------------------------------------ regions

    private bool CanAddRegion() => HasSelection;

    /// <summary>Creates a region over the selected text, optionally with a preset.</summary>
    [RelayCommand(CanExecute = nameof(CanAddRegion))]
    private async Task AddRegion(string? preset)
    {
        if (SelectedSample is not { } s || !HasSelection) return;
        var r = new Region { Start = SelectionStart, End = SelectionEnd };
        if (preset is not null) Presets.Apply(r, preset);
        s.Regions.Add(r);
        SelectedRegion = r;
        if (preset == "pronunciation") await LookupPronunciation();
    }

    /// <summary>Applies a preset to the selected region, or creates a region from the selection.</summary>
    [RelayCommand]
    private async Task ApplyPreset(string preset)
    {
        if (SelectedRegion is { } r)
        {
            if (preset == "pronunciation") await LookupPronunciation();
            else Presets.Apply(r, preset);
        }
        else if (HasSelection) await AddRegion(preset);
    }

    private bool HasRegion() => SelectedRegion is not null;

    [RelayCommand(CanExecute = nameof(HasRegion))]
    private void DeleteRegion()
    {
        if (SelectedSample is null || SelectedRegion is not { } r) return;
        SelectedSample.Regions.Remove(r);
        SelectedRegion = null;
    }

    [RelayCommand]
    private void ClearRegionProperties()
    {
        if (SelectedRegion is not { } r) return;
        r.CopyPropertiesFrom(new Region { Label = r.Label });
    }

    [RelayCommand]
    private void SelectRegion(Region? r) => SelectedRegion = r;

    [RelayCommand]
    private void DeselectRegion()
    {
        SelectedRegion = null;
        SelectionStart = SelectionEnd = 0;
    }

    /// <summary>Fills the region's pronunciation with the dictionary's phonemes as a starting point.</summary>
    [RelayCommand]
    private async Task LookupPronunciation()
    {
        if (SelectedSample is not { } s || SelectedRegion is not { } r) return;
        string words = s.TextOf(r).Trim();
        if (words.Length == 0) return;
        try
        {
            Status = $"Looking up “{words}”…";
            var client = await Engine.GetAsync();
            var voice = Voices.IsKnown(s.Voice) ? s.Voice! : Project.DefaultVoice;
            var result = await client.PhonemizeAsync(words, voice, Project.LexiconMap());
            r.Pronunciation = result.Tokens.Where(t => t.Source != "punct").Select(t => t.Phonemes).Where(p => p.Length > 0) is var ps && ps.Any()
                ? string.Join(" ", ps) : result.Phonemes;
            Status = $"Dictionary pronunciation of “{words}”: /{r.Pronunciation}/. Edit it to change how it is said.";
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or IOException)
        {
            Status = "Lookup failed: " + e.Message;
        }
    }

    [RelayCommand]
    private void AddToLexicon()
    {
        if (SelectedSample is not { } s || SelectedRegion is not { Pronunciation: { Length: > 0 } ps } r) return;
        string word = s.TextOf(r).Trim();
        var entry = Project.Lexicon.FirstOrDefault(e => string.Equals(e.Word, word, StringComparison.OrdinalIgnoreCase));
        if (entry is null) Project.Lexicon.Add(entry = new LexiconEntry { Word = word });
        entry.Phonemes = ps;
        Status = $"“{word}” = /{ps}/ added to the project lexicon; it now applies to every sample.";
    }

    [RelayCommand]
    private void AddLexiconEntry() => Project.Lexicon.Add(new LexiconEntry());

    [RelayCommand]
    private void RemoveLexiconEntry(LexiconEntry? entry)
    {
        if (entry is not null) Project.Lexicon.Remove(entry);
    }

    // ------------------------------------------------------------------ rendering and playback

    private string RenderKey(Sample s) => SynthesisRequest.For(Project, s, "").ContentKey();

    private void ScheduleRender()
    {
        if (_restoring || !AutoRender || SelectedSample is not { Text.Length: > 0 } || Engine.State != EngineState.Ready) return;
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void ShowCachedResult()
    {
        if (SelectedSample is { } s && _renders.TryGetValue(s.Id, out var entry))
        {
            Result = entry.Result;
            UpdateResultValidity(s);
        }
        else
        {
            Result = null;
            Tokens = null;
            IsResultStale = false;
        }
    }

    private void UpdateResultValidity(Sample s)
    {
        if (!_renders.TryGetValue(s.Id, out var entry) || s != SelectedSample) return;
        // Word positions are only meaningful while the text is unchanged.
        Tokens = entry.Text == s.Text ? entry.Result.Tokens : null;
        IsResultStale = entry.Key != RenderKey(s);
    }

    private async Task<SynthesisResult?> RenderAsync(Sample s, bool play, bool quiet = false, bool baseline = false)
    {
        if (string.IsNullOrWhiteSpace(s.Text))
        {
            if (!quiet) Status = "Type some text first (F2 edits the text).";
            return null;
        }
        string key = RenderKey(s);
        SynthesisResult result;
        if (!baseline && _renders.TryGetValue(s.Id, out var cached) && cached.Key == key && File.Exists(cached.Result.Output))
        {
            result = cached.Result;
        }
        else
        {
            int seq = ++_renderSeq;
            var request = SynthesisRequest.For(Project, s, Path.Combine(_renderDir, key + ".wav"),
                baseline ? Path.Combine(_renderDir, key + ".baseline.wav") : null);
            string text = s.Text;
            try
            {
                IsRendering = true;
                if (!quiet || Engine.State != EngineState.Ready) Status = Engine.State == EngineState.Ready ? "Rendering…" : "Starting the speech engine…";
                var client = await Engine.GetAsync();
                result = await client.SynthesizeAsync(request);
            }
            catch (Exception e) when (e is EngineException or InvalidOperationException or IOException or System.Net.Http.HttpRequestException)
            {
                Status = "Render failed: " + e.Message;
                if (!quiet) Dialog.ShowError(_window, "Render failed", e.Message);
                return null;
            }
            finally
            {
                if (seq == _renderSeq) IsRendering = false;
            }
            _renders[s.Id] = new RenderEntry(key, text, result);
            if (!quiet || Status.StartsWith("Render", StringComparison.Ordinal) || Status.StartsWith("Starting", StringComparison.Ordinal))
                Status = result.Warnings.Count > 0 ? $"Rendered with {result.Warnings.Count} warning(s): {result.Warnings[0]}" : "Rendered";
        }
        if (s == SelectedSample)
        {
            Result = result;
            UpdateResultValidity(s);
        }
        if (play)
        {
            string? file = baseline ? result.Baseline?.Output : result.Output;
            if (file is not null) Player.Play(file);
        }
        return result;
    }

    private void RenderCommandsChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        PlayBaselineCommand.NotifyCanExecuteChanged();
        ExportSampleCommand.NotifyCanExecuteChanged();
    }

    private bool HasSample() => SelectedSample is not null;

    /// <summary>Renders (if needed) and plays the selected sample; stops if it is already playing.</summary>
    [RelayCommand(CanExecute = nameof(HasSample))]
    private async Task Play()
    {
        if (Player.IsPlaying)
        {
            Player.Stop();
            return;
        }
        if (SelectedSample is { } s) await RenderAsync(s, play: true);
    }

    [RelayCommand]
    private void Stop() => Player.Stop();

    /// <summary>Plays the sample with every prosody edit switched off (pronunciations kept), for A/B listening.</summary>
    [RelayCommand(CanExecute = nameof(HasSample))]
    private async Task PlayBaseline()
    {
        Player.Stop();
        if (SelectedSample is { } s && await RenderAsync(s, play: true, baseline: true) is not null)
            Status = "Playing the baseline: same text and pronunciations, no prosody edits.";
    }

    [RelayCommand(CanExecute = nameof(HasRegion))]
    private async Task PlayRegion()
    {
        if (SelectedSample is not { } s || SelectedRegion is not { } r) return;
        var result = await RenderAsync(s, play: false);
        var hit = result?.Tokens.Where(t => t.Start is not null && t.CharStart < r.End && t.CharEnd > r.Start).ToList();
        if (result is null || hit is null || hit.Count == 0) return;
        Player.Play(result.Output, Math.Max(0, hit.Min(t => t.Start!.Value) - 0.05));
        double until = hit.Max(t => t.End!.Value) + 0.15;
        var stopper = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        stopper.Tick += (_, _) =>
        {
            if (!Player.IsPlaying || Player.Position >= until)
            {
                if (Player.IsPlaying) Player.Stop();
                stopper.Stop();
            }
        };
        stopper.Start();
    }

    [RelayCommand]
    private void Seek(double seconds)
    {
        if (Result is { } r && File.Exists(r.Output)) Player.Play(r.Output, seconds);
    }

    [RelayCommand(CanExecute = nameof(HasSample))]
    private async Task ExportSample()
    {
        if (SelectedSample is not { } s) return;
        string fmt = Exporter.NormalizeFormat(Project.ExportFormat);
        var dlg = new SaveFileDialog
        {
            Title = "Export sample audio",
            FileName = Exporter.SafeFileName(s.ExportName) + "." + fmt,
            Filter = "WAV audio (*.wav)|*.wav|FLAC audio (*.flac)|*.flac|Ogg Vorbis (*.ogg)|*.ogg|MP3 audio (*.mp3)|*.mp3",
            FilterIndex = Exporter.Formats.ToList().IndexOf(fmt) + 1,
        };
        if (ProjectPath is not null) dlg.InitialDirectory = Exporter.ExportFolder(Project, ProjectPath) is var f && Directory.Exists(f) ? f : Path.GetDirectoryName(ProjectPath);
        if (dlg.ShowDialog(_window) != true) return;
        try
        {
            Status = $"Exporting “{s.Name}”…";
            var client = await Engine.GetAsync();
            var result = await client.SynthesizeAsync(SynthesisRequest.For(Project, s, dlg.FileName));
            Status = $"Exported {Path.GetFileName(result.Output)} ({result.Duration:0.0} s)";
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or IOException)
        {
            Dialog.ShowError(_window, "Export failed", e.Message);
            Status = "Export failed";
        }
    }

    [RelayCommand]
    private async Task ExportAll()
    {
        if (Project.Samples.Count == 0) return;
        if (ProjectPath is null && !Path.IsPathRooted(Project.ExportFolder))
        {
            if (Dialog.Show(_window, "Save the project first",
                    $"Exports go to the “{Project.ExportFolder}” folder next to the project file, so the project needs saving first.",
                    "Save", "Cancel") != 0 || !Save())
                return;
        }
        try
        {
            var client = await Engine.GetAsync();
            var progress = new Progress<(int Done, int Total, Sample Sample)>(p => Status = $"Exporting {p.Done + 1} of {p.Total}: {p.Sample.Name}…");
            var results = await Exporter.ExportAllAsync(client, Project, ProjectPath, progress: progress);
            string folder = Exporter.ExportFolder(Project, ProjectPath);
            int warnings = results.Sum(r => r.Result.Warnings.Count);
            Status = $"Exported {results.Count} sample(s) to {folder}";
            if (Dialog.Show(_window, "Export complete",
                    $"Exported {results.Count} sample(s) as {Project.ExportFormat.ToUpperInvariant()} to\n{folder}" +
                    (warnings > 0 ? $"\n\n{warnings} pronunciation warning(s); check words underlined in red." : ""),
                    "Open folder", "Close") == 0)
                OpenFolder(folder);
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or IOException or ArgumentException)
        {
            Dialog.ShowError(_window, "Export failed", e.Message);
            Status = "Export failed";
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        string folder = Exporter.ExportFolder(Project, ProjectPath);
        Directory.CreateDirectory(folder);
        OpenFolder(folder);
    }

    private static void OpenFolder(string folder) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    [RelayCommand]
    private void ToggleEditText() => IsEditingText = !IsEditingText && SelectedSample is not null;

    // ------------------------------------------------------------------ engine and help

    [RelayCommand]
    private async Task StartEngine()
    {
        try
        {
            await Engine.GetAsync();
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or IOException or System.Net.Http.HttpRequestException)
        {
            Status = "The speech engine could not start: " + e.Message;
        }
    }

    [RelayCommand]
    private void ShowEngineLog() => new EngineLogWindow(Engine) { Owner = _window }.Show();

    [RelayCommand]
    private void ShowMcpSetup()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TtsStudio.Mcp.exe");
        string json = JsonSerializer.Serialize(new { mcpServers = new { tts_studio = new { command = exe, args = Array.Empty<string>() } } },
            new JsonSerializerOptions { WriteIndented = true }).Replace("tts_studio", "tts-studio");
        Dialog.ShowText(_window, "Connect a coding agent (MCP)",
            "TTS Studio includes an MCP server so coding agents can create and edit projects, look up pronunciations and render audio. " +
            "Changes the agent saves show up here automatically.",
            $"Claude Code:\n  claude mcp add tts-studio -- \"{exe}\"\n\nOther MCP clients (e.g. .mcp.json):\n{json}", 230);
    }

    [RelayCommand]
    private void ShowGuide() => Dialog.ShowText(_window, "Speech reference", "Region properties, the phoneme set and the quick markup.", Guide.Full, 420);

    private void CleanRenderCache()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_renderDir).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)))
                File.Delete(f);
        }
        catch (IOException)
        {
        }
    }

    public void Shutdown()
    {
        Player.Stop();
        _fileWatcher?.Dispose();
        Settings.Save();
    }
}
