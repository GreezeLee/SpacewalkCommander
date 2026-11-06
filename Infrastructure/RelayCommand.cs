using System.Windows.Input;

namespace UrevoSpacewalkCommander.Infrastructure;

/// <summary>
/// A command that runs a delegate. The delegate must not throw; the view model methods behind the commands
/// catch and report their own errors.
/// </summary>
internal sealed class RelayCommand : ICommand
{
  /// <summary>The action to run.</summary>
  private readonly Func<Task> _execute;

  /// <summary>Decides whether the command is currently available; <see langword="null"/> means always.</summary>
  private readonly Func<bool>? _canExecute;

  /// <summary>
  /// Creates a command that runs an asynchronous action.
  /// </summary>
  /// <param name="execute">The action to run.</param>
  /// <param name="canExecute">Optional availability condition.</param>
  public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
  {
    _execute = execute;
    _canExecute = canExecute;
  }

  /// <summary>
  /// Creates a command that runs a synchronous action.
  /// </summary>
  /// <param name="execute">The action to run.</param>
  /// <param name="canExecute">Optional availability condition.</param>
  public RelayCommand(Action execute, Func<bool>? canExecute = null)
    : this(() => RunSynchronously(execute), canExecute)
  {
  }

  /// <inheritdoc />
  public event EventHandler? CanExecuteChanged
  {
    add => CommandManager.RequerySuggested += value;
    remove => CommandManager.RequerySuggested -= value;
  }

  /// <inheritdoc />
  public bool CanExecute(object? parameter)
  {
    return _canExecute?.Invoke() ?? true;
  }

  /// <inheritdoc />
  public async void Execute(object? parameter)
  {
    await _execute();
  }

  /// <summary>
  /// Adapts a synchronous action to the asynchronous delegate type.
  /// </summary>
  /// <param name="execute">The action to run.</param>
  /// <returns>A completed task.</returns>
  private static Task RunSynchronously(Action execute)
  {
    execute();
    return Task.CompletedTask;
  }
}
