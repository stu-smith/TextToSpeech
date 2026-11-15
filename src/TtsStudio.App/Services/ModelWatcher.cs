using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Services;

/// <summary>Raises <see cref="Changed"/> for any change anywhere in a project (properties or collections).</summary>
public sealed class ModelWatcher : IDisposable
{
    private readonly HashSet<object> _attached = new(ReferenceEqualityComparer.Instance);
    private Project? _project;

    /// <summary>The object that changed, and the property name (null for collection changes).</summary>
    public event Action<object, string?>? Changed;

    public void Watch(Project? project)
    {
        foreach (var o in _attached.ToList()) Detach(o);
        _project = project;
        if (project is not null) Attach(project);
    }

    private void Attach(object o)
    {
        if (!_attached.Add(o)) return;
        if (o is INotifyPropertyChanged npc) npc.PropertyChanged += OnPropertyChanged;
        if (o is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnCollectionChanged;
        foreach (var child in Children(o)) Attach(child);
    }

    private void Detach(object o)
    {
        if (!_attached.Remove(o)) return;
        if (o is INotifyPropertyChanged npc) npc.PropertyChanged -= OnPropertyChanged;
        if (o is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnCollectionChanged;
        foreach (var child in Children(o)) Detach(child);
    }

    private static IEnumerable<object> Children(object o) => o switch
    {
        Project p => [p.Samples, p.Lexicon],
        Sample s => [s.Regions],
        IEnumerable list and INotifyCollectionChanged => list.Cast<object>(),
        _ => [],
    };

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke(sender!, e.PropertyName);

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Watch(_project); // the old items are gone without notice; re-attach the whole tree
        }
        else
        {
            foreach (var o in e.OldItems ?? Array.Empty<object>()) Detach(o);
            foreach (var o in e.NewItems ?? Array.Empty<object>()) Attach(o);
        }
        Changed?.Invoke(sender!, null);
    }

    public void Dispose() => Watch(null);
}
