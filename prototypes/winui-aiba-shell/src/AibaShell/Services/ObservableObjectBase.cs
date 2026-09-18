using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AibaShell.Services;

/// <summary>
/// Minimal INotifyPropertyChanged base. The prototype deliberately hand-rolls this
/// instead of leaning on a toolkit so the model layer has zero UI dependencies.
/// </summary>
public abstract class ObservableObjectBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected internal void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
