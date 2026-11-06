namespace UrevoSpacewalkCommander.Infrastructure;

/// <summary>
/// Runs an action only after calls to <see cref="Schedule"/> stopped for a while. Used so that dragging a slider
/// sends one command to the treadmill instead of dozens.
/// </summary>
/// <remarks>Must be used from the UI thread; the action then also runs on the UI thread.</remarks>
internal sealed class Debouncer
{
  /// <summary>How long calls must pause before the action runs.</summary>
  private readonly TimeSpan _delay;

  /// <summary>Cancels the currently waiting action; <see langword="null"/> if none waits.</summary>
  private CancellationTokenSource? _pending;

  /// <summary>
  /// Creates a debouncer.
  /// </summary>
  /// <param name="delay">How long calls must pause before the action runs.</param>
  public Debouncer(TimeSpan delay)
  {
    _delay = delay;
  }

  /// <summary>
  /// Replaces any waiting action by <paramref name="action"/> and runs it after the delay.
  /// </summary>
  /// <param name="action">The action; it must handle its own errors.</param>
  public void Schedule(Func<Task> action)
  {
    Cancel();
    var source = new CancellationTokenSource();
    _pending = source;
    _ = RunAfterDelayAsync(action, source.Token);
  }

  /// <summary>
  /// Drops the waiting action, if any.
  /// </summary>
  public void Cancel()
  {
    _pending?.Cancel();
    _pending?.Dispose();
    _pending = null;
  }

  /// <summary>
  /// Waits for the delay and runs the action unless it was replaced or cancelled meanwhile.
  /// </summary>
  /// <param name="action">The action to run.</param>
  /// <param name="cancellationToken">Cancelled when the action was replaced.</param>
  private async Task RunAfterDelayAsync(Func<Task> action, CancellationToken cancellationToken)
  {
    try
    {
      await Task.Delay(_delay, cancellationToken);
    }
    catch (OperationCanceledException)
    {
      return;
    }

    await action();
  }
}
