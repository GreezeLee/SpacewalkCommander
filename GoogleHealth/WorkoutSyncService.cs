using System.IO;
using UrevoSpacewalkCommander.Workouts;

namespace UrevoSpacewalkCommander.GoogleHealth;

/// <summary>
/// Uploads archived workouts to Google Health. Every workout is archived first, so a workout survives a missing
/// sign-in, a network failure or closing the application, and is uploaded on the next attempt.
/// </summary>
/// <remarks>
/// Calls are serialized, so a workout finished during an upload does not race with it. Uploads never start an
/// interactive sign-in; only <see cref="SignInAsync"/> opens the browser.
/// </remarks>
internal sealed class WorkoutSyncService
{
  /// <summary>Serializes uploads and sign-ins.</summary>
  private readonly SemaphoreSlim _lock = new(1, 1);

  private readonly WorkoutStore _store;
  private readonly GoogleAuthorization? _authorization;
  private readonly GoogleHealthClient _client;
  private readonly double? _bodyWeightKg;
  private readonly Action<string> _log;

  /// <summary>
  /// Creates the service.
  /// </summary>
  /// <param name="store">The workout archive.</param>
  /// <param name="authorization">The Google sign-in; <see langword="null"/> if Google Health is not set up.</param>
  /// <param name="client">The Google Health API client.</param>
  /// <param name="bodyWeightKg">Body weight for the energy estimate, if configured.</param>
  /// <param name="log">Receives progress lines; may be called on any thread.</param>
  public WorkoutSyncService(
    WorkoutStore store,
    GoogleAuthorization? authorization,
    GoogleHealthClient client,
    double? bodyWeightKg,
    Action<string> log)
  {
    _store = store;
    _authorization = authorization;
    _client = client;
    _bodyWeightKg = bodyWeightKg;
    _log = log;
  }

  /// <summary>Gets a value indicating whether a Google OAuth client is configured.</summary>
  public bool IsConfigured => _authorization is not null;

  /// <summary>Gets a value indicating whether the user signed in earlier and the sign-in is still stored.</summary>
  public bool HasStoredSignIn => _authorization?.HasStoredSignIn ?? false;

  /// <summary>Gets the number of workouts that still have to be uploaded.</summary>
  public int PendingCount => _store.GetPendingFiles().Count;

  /// <summary>
  /// Archives a finished workout and uploads all pending workouts.
  /// </summary>
  /// <param name="session">The finished workout.</param>
  /// <param name="cancellationToken">Cancels the upload; the workout stays archived.</param>
  /// <returns>The outcome.</returns>
  public async Task<SyncResult> SubmitAsync(WorkoutSession session, CancellationToken cancellationToken)
  {
    string path = _store.SavePending(session);
    _log($"Workout saved: {Path.GetFileName(path)}");
    return await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Signs in in the browser, verifies that the account can use Google Health and uploads pending workouts.
  /// </summary>
  /// <param name="cancellationToken">Cancels the sign-in.</param>
  /// <returns>The outcome of the following upload.</returns>
  /// <exception cref="InvalidOperationException">Google Health is not configured.</exception>
  public async Task<SyncResult> SignInAsync(CancellationToken cancellationToken)
  {
    GoogleAuthorization authorization = _authorization
      ?? throw new InvalidOperationException("Google Health is not configured.");
    await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await authorization.SignInAsync(cancellationToken).ConfigureAwait(false);
      string token = await authorization.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Google did not issue an access token.");
      await _client.CheckIdentityAsync(token, cancellationToken).ConfigureAwait(false);
      _log("Signed in to Google Health.");
    }
    finally
    {
      _lock.Release();
    }

    return await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Uploads all pending workouts, oldest first. A workout that fails stays pending; the others are still tried,
  /// unless the failure concerns the account as a whole.
  /// </summary>
  /// <param name="cancellationToken">Cancels the upload.</param>
  /// <returns>The outcome.</returns>
  public async Task<SyncResult> UploadPendingAsync(CancellationToken cancellationToken)
  {
    await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      IReadOnlyList<string> files = _store.GetPendingFiles();
      if (files.Count == 0)
      {
        return new SyncResult(0, 0, SyncState.Done);
      }

      if (_authorization is null)
      {
        return new SyncResult(0, files.Count, SyncState.NotConfigured);
      }

      return await UploadFilesAsync(_authorization, files, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Uploads the given files with a valid access token.
  /// </summary>
  /// <param name="authorization">The Google sign-in.</param>
  /// <param name="files">The pending files.</param>
  /// <param name="cancellationToken">Cancels the upload.</param>
  /// <returns>The outcome.</returns>
  private async Task<SyncResult> UploadFilesAsync(
    GoogleAuthorization authorization,
    IReadOnlyList<string> files,
    CancellationToken cancellationToken)
  {
    int uploaded = 0;
    foreach (string file in files)
    {
      string? token = await authorization.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
      if (token is null)
      {
        return new SyncResult(uploaded, files.Count - uploaded, SyncState.SignInRequired);
      }

      try
      {
        await UploadFileAsync(authorization, file, token, cancellationToken).ConfigureAwait(false);
        uploaded++;
      }
      catch (GoogleHealthException exception) when (exception.IsAccountNotLinked)
      {
        _log(exception.Message);
        return new SyncResult(uploaded, files.Count - uploaded, SyncState.AccountNotLinked);
      }
      catch (Exception exception) when (exception is GoogleHealthException or IOException
        or InvalidDataException or TimeoutException or System.Net.Http.HttpRequestException)
      {
        _log($"Upload of {Path.GetFileName(file)} failed: {exception.Message}");
      }
    }

    int pending = files.Count - uploaded;
    return new SyncResult(uploaded, pending, pending == 0 ? SyncState.Done : SyncState.Failed);
  }

  /// <summary>
  /// Uploads one archived workout. A rejected access token is renewed once; this is safe because the API did
  /// not create anything for the rejected request.
  /// </summary>
  /// <param name="authorization">The Google sign-in.</param>
  /// <param name="file">The pending file.</param>
  /// <param name="token">A valid access token.</param>
  /// <param name="cancellationToken">Cancels the upload.</param>
  private async Task UploadFileAsync(
    GoogleAuthorization authorization,
    string file,
    string token,
    CancellationToken cancellationToken)
  {
    WorkoutSession session = WorkoutStore.Load(file);
    WorkoutSummary summary = WorkoutAnalyzer.Analyze(session, _bodyWeightKg);
    ExerciseDataPointDto dataPoint = ExercisePayloadBuilder.Build(session, summary);
    OperationDto operation;
    try
    {
      operation = await _client.CreateExerciseAsync(dataPoint, token, cancellationToken).ConfigureAwait(false);
    }
    catch (GoogleHealthException exception) when (exception.IsUnauthorized)
    {
      authorization.InvalidateAccessToken();
      string? renewed = await authorization.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
      if (renewed is null)
      {
        throw;
      }

      operation = await _client.CreateExerciseAsync(dataPoint, renewed, cancellationToken).ConfigureAwait(false);
    }

    _store.MarkUploaded(file);
    string state = operation.Done ? "saved" : "accepted (still processing)";
    _log($"Google Health: workout of {session.StartTime:yyyy-MM-dd HH:mm} {state}.");
  }
}

/// <summary>
/// Overall outcome of an upload attempt.
/// </summary>
internal enum SyncState
{
  /// <summary>All pending workouts were uploaded (or there were none).</summary>
  Done,

  /// <summary>Some workouts failed and stay pending.</summary>
  Failed,

  /// <summary>No Google OAuth client is configured; workouts are only archived.</summary>
  NotConfigured,

  /// <summary>The user has to sign in (first time, or the stored sign-in expired).</summary>
  SignInRequired,

  /// <summary>The Google account has never been set up in the Google Health app.</summary>
  AccountNotLinked,
}

/// <summary>
/// Outcome of an upload attempt.
/// </summary>
/// <param name="Uploaded">Number of workouts uploaded.</param>
/// <param name="Pending">Number of workouts still pending.</param>
/// <param name="State">Overall outcome.</param>
internal sealed record SyncResult(int Uploaded, int Pending, SyncState State);
