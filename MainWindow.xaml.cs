using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using SpacewalkCommander.Infrastructure;
using SpacewalkCommander.ViewModels;

namespace SpacewalkCommander;

/// <summary>
/// The main window. Holds no logic besides view concerns: culture of the bindings, the dark title bar, log
/// scrolling and the confirmation before closing while the belt runs.
/// </summary>
public partial class MainWindow : Window
{
  /// <summary>The view model behind the window.</summary>
  private readonly MainViewModel _viewModel = new();

  /// <summary>Set once the user confirmed closing and the shutdown work is done.</summary>
  private bool _closeConfirmed;

  /// <summary>
  /// Creates the window and connects it to its view model.
  /// </summary>
  public MainWindow()
  {
    InitializeComponent();

    // Bindings format numbers with the language of the element; make it the user's culture (decimal separator).
    Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
    DataContext = _viewModel;
    _viewModel.LogEntries.CollectionChanged += OnLogChanged;
    Closing += OnClosing;
    Loaded += OnLoaded;
    SourceInitialized += OnSourceInitialized;
  }

  /// <summary>
  /// Colours the native title bar like the window background as soon as the window has a handle, so the title bar
  /// and the content look like one dark surface.
  /// </summary>
  /// <param name="sender">The window.</param>
  /// <param name="e">Unused.</param>
  private void OnSourceInitialized(object? sender, EventArgs e)
  {
    DarkTitleBar.Apply(
      this,
      (Color)FindResource("WindowColor"),
      (Color)FindResource("TextColor"),
      (Color)FindResource("BorderColor"));
  }

  /// <summary>
  /// Looks for a Urevo / Spacewalk treadmill as soon as the window is shown and connects to it. Workouts left
  /// over from earlier sessions are uploaded to Google Health in parallel.
  /// </summary>
  /// <param name="sender">The window.</param>
  /// <param name="e">Unused.</param>
  private async void OnLoaded(object sender, RoutedEventArgs e)
  {
    Task upload = _viewModel.WorkoutSync.UploadPendingAsync();
    await _viewModel.AutoConnectAsync();
    await upload;
  }

  /// <summary>
  /// Keeps the newest log line visible.
  /// </summary>
  /// <param name="sender">The log collection.</param>
  /// <param name="e">Describes the change.</param>
  private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
  {
    if (e.Action == NotifyCollectionChangedAction.Add && _viewModel.LogEntries.Count > 0)
    {
      LogList.ScrollIntoView(_viewModel.LogEntries[^1]);
    }
  }

  /// <summary>
  /// Asks before closing while the belt runs, stops it if the user agrees and releases Bluetooth before the
  /// window closes.
  /// </summary>
  /// <param name="sender">The window.</param>
  /// <param name="e">Allows cancelling the close.</param>
  private async void OnClosing(object? sender, CancelEventArgs e)
  {
    if (_closeConfirmed)
    {
      return;
    }

    e.Cancel = true;
    bool wasRunning = _viewModel.IsRunning;
    if (wasRunning && !ConfirmStopAndClose())
    {
      return;
    }

    await _viewModel.ShutdownAsync(stopBelt: wasRunning);
    _closeConfirmed = true;
    Close();
  }

  /// <summary>
  /// Asks whether the running belt may be stopped so the application can close.
  /// </summary>
  /// <returns><see langword="true"/> if the user agreed.</returns>
  private bool ConfirmStopAndClose()
  {
    MessageBoxResult answer = MessageBox.Show(
      this,
      "The treadmill is still running. Stop it and exit?",
      Title,
      MessageBoxButton.YesNo,
      MessageBoxImage.Question);
    return answer == MessageBoxResult.Yes;
  }
}
