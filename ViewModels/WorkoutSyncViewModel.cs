using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using UrevoSpacewalkCommander.Configuration;
using UrevoSpacewalkCommander.GoogleHealth;
using UrevoSpacewalkCommander.Infrastructure;
using UrevoSpacewalkCommander.Workouts;

namespace UrevoSpacewalkCommander.ViewModels;

/// <summary>
/// State and commands of the "Google Health" area: sign-in, upload of pending workouts, and the status line.
/// </summary>
/// <remarks>Used from the UI thread; the log callback marshals itself.</remarks>
internal sealed class WorkoutSyncViewModel : ObservableObject
{
  /// <summary>Upper bound for a single HTTP request to Google.</summary>
  private static readonly TimeSpan s_httpTimeout = TimeSpan.FromSeconds(30);

  /// <summary>Shared HTTP client for the token endpoint and the API.</summary>
  private static readonly HttpClient s_http = new() { Timeout = s_httpTimeout };

  /// <summary>Uploads workouts; <see langword="null"/> if the settings could not be read.</summary>
  private readonly WorkoutSyncService? _service;

  private readonly Action<string> _log;

  /// <summary>Whether a sign-in or upload is in progress.</summary>
  private bool _isBusy;

  /// <summary>Backing field of <see cref="StatusText"/>.</summary>
  private string _statusText = string.Empty;

  /// <summary>
  /// Loads the settings and prepares the upload.
  /// </summary>
  /// <param name="log">Writes a line to the application log; may be called on any thread.</param>
  public WorkoutSyncViewModel(Action<string> log)
  {
    _log = log;
    try
    {
      AppSettings settings = AppSettings.Load();
      BodyWeightKg = settings.BodyWeightKg;
      GoogleAuthorization? authorization = settings.GoogleClient is { } client
        ? new GoogleAuthorization(s_http, client, new TokenStore(AppSettings.DataDirectory))
        : null;
      _service = new WorkoutSyncService(
        new WorkoutStore(Path.Combine(AppSettings.DataDirectory, "workouts")),
        authorization,
        new GoogleHealthClient(s_http),
        settings.BodyWeightKg,
        log);
      StatusText = DescribeIdleState();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
      or InvalidDataException)
    {
      StatusText = $"Settings cannot be read: {exception.Message}";
    }

    SignInCommand = new RelayCommand(SignInAsync, () => _service?.IsConfigured == true && !IsBusy);
    UploadPendingCommand = new RelayCommand(UploadPendingAsync, () => _service is not null && !IsBusy);
    OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
  }

  /// <summary>Gets the body weight for the energy estimate, if configured.</summary>
  public double? BodyWeightKg { get; }

  /// <summary>Gets the command that signs in to Google in the browser.</summary>
  public ICommand SignInCommand { get; }

  /// <summary>Gets the command that uploads pending workouts.</summary>
  public ICommand UploadPendingCommand { get; }

  /// <summary>Gets the command that opens the settings folder in Explorer.</summary>
  public ICommand OpenSettingsFolderCommand { get; }

  /// <summary>Gets a value indicating whether a sign-in or upload is in progress.</summary>
  public bool IsBusy
  {
    get => _isBusy;
    private set => SetProperty(ref _isBusy, value);
  }

  /// <summary>Gets the status line of the Google Health area.</summary>
  public string StatusText
  {
    get => _statusText;
    private set => SetProperty(ref _statusText, value);
  }

  /// <summary>
  /// Archives a finished workout and uploads it with all other pending ones. Never throws.
  /// </summary>
  /// <param name="session">The finished workout.</param>
  /// <param name="cancellationToken">Cancels the upload; the workout stays archived.</param>
  public Task SubmitAsync(WorkoutSession session, CancellationToken cancellationToken)
  {
    WorkoutSyncService? service = _service;
    return service is null
      ? Task.CompletedTask
      : RunAsync("Upload",() => service.SubmitAsync(session, cancellationToken));
  }

  /// <summary>
  /// Uploads pending workouts without user interaction, for example at start-up. Never throws.
  /// </summary>
  public Task UploadPendingAsync()
  {
    WorkoutSyncService? service = _service;
    return service is null
      ? Task.CompletedTask
      : RunAsync("Upload",() => service.UploadPendingAsync(CancellationToken.None));
  }

  /// <summary>
  /// Signs in in the browser, then uploads pending workouts. Never throws.
  /// </summary>
  private Task SignInAsync()
  {
    WorkoutSyncService? service = _service;
    StatusText = "Sign-in in the browser in progress ...";
    return service is null
      ? Task.CompletedTask
      : RunAsync("Sign-in",() => service.SignInAsync(CancellationToken.None));
  }

  /// <summary>
  /// Runs a sign-in or upload, shows its outcome and reports failures in the log instead of throwing.
  /// </summary>
  /// <param name="action">Name of the action for messages.</param>
  /// <param name="work">The work.</param>
  private async Task RunAsync(string action, Func<Task<SyncResult>> work)
  {
    IsBusy = true;
    try
    {
      SyncResult result = await work();
      StatusText = DescribeResult(result);
    }
    catch (Exception exception)
    {
      _log($"Google Health: {action} failed: {exception.Message}");
      StatusText = $"{action} failed: {exception.Message}";
    }
    finally
    {
      IsBusy = false;
    }
  }

  /// <summary>
  /// Describes the state before any upload.
  /// </summary>
  /// <returns>The status text.</returns>
  private string DescribeIdleState()
  {
    if (_service is not { } service)
    {
      return string.Empty;
    }

    if (!service.IsConfigured)
    {
      return "Not set up: put the OAuth client into the settings folder. Workouts are saved locally.";
    }

    string pending = service.PendingCount > 0 ? $" {service.PendingCount} workout(s) pending." : string.Empty;
    return (service.HasStoredSignIn ? "Signed in." : "Not signed in.") + pending;
  }

  /// <summary>
  /// Describes the outcome of an upload for the status line.
  /// </summary>
  /// <param name="result">The outcome.</param>
  /// <returns>The status text.</returns>
  private static string DescribeResult(SyncResult result)
  {
    string uploaded = result.Uploaded > 0 ? $"{result.Uploaded} workout(s) uploaded. " : string.Empty;
    return result.State switch
    {
      SyncState.Done => uploaded + "Everything is synchronized.",
      SyncState.Failed => uploaded + $"{result.Pending} workout(s) still pending (see log).",
      SyncState.NotConfigured => $"Not set up, {result.Pending} workout(s) saved locally.",
      SyncState.SignInRequired => uploaded + $"Sign-in required, {result.Pending} workout(s) pending.",
      SyncState.AccountNotLinked =>
        "The Google account is not linked to Google Health: sign in to the Google Health app once.",
      _ => string.Empty,
    };
  }

  /// <summary>
  /// Opens the folder with <c>settings.json</c> and the OAuth client file in Explorer.
  /// </summary>
  private void OpenSettingsFolder()
  {
    Directory.CreateDirectory(AppSettings.SettingsDirectory);
    Process.Start(new ProcessStartInfo(AppSettings.SettingsDirectory) { UseShellExecute = true });
  }
}
