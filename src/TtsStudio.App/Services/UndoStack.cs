namespace TtsStudio.App.Services;

/// <summary>
/// Snapshot-based undo: the caller records the serialized project after each burst of edits;
/// undo and redo hand back earlier or later snapshots to restore.
/// </summary>
public sealed class UndoStack
{
    private const int Limit = 200;
    private readonly List<string> _undo = [];
    private readonly Stack<string> _redo = new();

    public bool CanUndo => _undo.Count > 1;
    public bool CanRedo => _redo.Count > 0;

    public void Reset(string snapshot)
    {
        _undo.Clear();
        _redo.Clear();
        _undo.Add(snapshot);
    }

    /// <summary>Records a new state; returns false if it is identical to the current one.</summary>
    public bool Record(string snapshot)
    {
        if (_undo.Count > 0 && _undo[^1] == snapshot) return false;
        _undo.Add(snapshot);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        return true;
    }

    public string? Undo()
    {
        if (!CanUndo) return null;
        _redo.Push(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return _undo[^1];
    }

    public string? Redo()
    {
        if (!CanRedo) return null;
        _undo.Add(_redo.Pop());
        return _undo[^1];
    }
}
