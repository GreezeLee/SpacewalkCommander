using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// Archives workouts as JSON files with their complete raw recording. New workouts go to <c>pending</c>; after a
/// successful upload they are moved to <c>uploaded</c>, so the local archive keeps every workout.
/// </summary>
internal sealed class WorkoutStore
{
  /// <summary>Folder name for workouts that still have to be uploaded.</summary>
  private const string PendingFolderName = "pending";

  /// <summary>Folder name for uploaded workouts.</summary>
  private const string UploadedFolderName = "uploaded";

  /// <summary>File pattern of archived workouts.</summary>
  private const string WorkoutFilePattern = "*.json";

  /// <summary>Format of the archive files: readable, camelCase, enums as names.</summary>
  private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
  };

  /// <summary>Folder of the workouts still to be uploaded.</summary>
  private readonly string _pendingDirectory;

  /// <summary>Folder of the uploaded workouts.</summary>
  private readonly string _uploadedDirectory;

  /// <summary>
  /// Creates a store below <paramref name="rootDirectory"/>.
  /// </summary>
  /// <param name="rootDirectory">The folder that contains <c>pending</c> and <c>uploaded</c>.</param>
  public WorkoutStore(string rootDirectory)
  {
    _pendingDirectory = Path.Combine(rootDirectory, PendingFolderName);
    _uploadedDirectory = Path.Combine(rootDirectory, UploadedFolderName);
  }

  /// <summary>
  /// Saves a finished workout as pending upload.
  /// </summary>
  /// <param name="session">The workout.</param>
  /// <returns>The path of the written file.</returns>
  public string SavePending(WorkoutSession session)
  {
    Directory.CreateDirectory(_pendingDirectory);
    string fileName = string.Create(
      CultureInfo.InvariantCulture,
      $"{session.StartTime:yyyyMMdd-HHmmss}-{session.Id.ToString("N")[..8]}.json");
    string path = Path.Combine(_pendingDirectory, fileName);
    File.WriteAllText(path, JsonSerializer.Serialize(session, s_jsonOptions));
    return path;
  }

  /// <summary>
  /// Lists the workouts that still have to be uploaded, oldest first.
  /// </summary>
  /// <returns>The file paths; empty if there are none.</returns>
  public IReadOnlyList<string> GetPendingFiles()
  {
    return Directory.Exists(_pendingDirectory)
      ? [.. Directory.GetFiles(_pendingDirectory, WorkoutFilePattern).Order(StringComparer.Ordinal)]
      : [];
  }

  /// <summary>
  /// Reads an archived workout.
  /// </summary>
  /// <param name="path">The file path.</param>
  /// <returns>The workout.</returns>
  /// <exception cref="InvalidDataException">The file does not contain a workout.</exception>
  public static WorkoutSession Load(string path)
  {
    return JsonSerializer.Deserialize<WorkoutSession>(File.ReadAllText(path), s_jsonOptions)
      ?? throw new InvalidDataException($"'{path}' does not contain a workout.");
  }

  /// <summary>
  /// Moves an uploaded workout from <c>pending</c> to <c>uploaded</c>.
  /// </summary>
  /// <param name="path">The pending file.</param>
  public void MarkUploaded(string path)
  {
    Directory.CreateDirectory(_uploadedDirectory);
    File.Move(path, Path.Combine(_uploadedDirectory, Path.GetFileName(path)), overwrite: true);
  }
}
