using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UrevoSpacewalkCommander.Infrastructure;

/// <summary>
/// Base class for view models: implements property change notification.
/// </summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
  /// <inheritdoc />
  public event PropertyChangedEventHandler? PropertyChanged;

  /// <summary>
  /// Stores a new value and notifies the UI if it differs from the current one. Also asks WPF to re-evaluate
  /// whether commands can execute, because most command conditions depend on view model state.
  /// </summary>
  /// <typeparam name="T">Type of the property.</typeparam>
  /// <param name="field">The backing field.</param>
  /// <param name="value">The new value.</param>
  /// <param name="propertyName">Name of the property; filled in by the compiler.</param>
  /// <returns><see langword="true"/> if the value changed.</returns>
  protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
  {
    if (EqualityComparer<T>.Default.Equals(field, value))
    {
      return false;
    }

    field = value;
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    CommandManager.InvalidateRequerySuggested();
    return true;
  }
}
