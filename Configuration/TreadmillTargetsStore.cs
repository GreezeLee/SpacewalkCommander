using System.IO;
using System.Text.Json;

namespace UrevoSpacewalkCommander.Configuration;

/// <summary>
/// Remembers the target speed and incline last selected by the user in <c>last-targets.json</c>, so that the next
/// start of the application preselects them. The file is written by the application only; it is kept apart from the
/// hand-edited <c>settings.json</c> so that saving never rewrites the user's settings.
/// </summary>
internal sealed class TreadmillTargetsStore
{
  /// <summary>Name of the file holding the last targets.</summary>
  private const string FileName = "last-targets.json";

  /// <summary>Serializer options: camelCase keys, indented for readability; a missing value is an error.</summary>
  private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true,
    RespectRequiredConstructorParameters = true,
  };

  /// <summary>Full path of the targets file.</summary>
  private readonly string _path;

  /// <summary>
  /// Creates the store.
  /// </summary>
  /// <param name="directory">Folder of the targets file; created on the first save.</param>
  public TreadmillTargetsStore(string directory)
  {
    _path = Path.Combine(directory, FileName);
  }

  /// <summary>
  /// Reads the last targets.
  /// </summary>
  /// <returns>The targets, or <see langword="null"/> if none were saved yet.</returns>
  /// <exception cref="InvalidDataException">The file is not valid JSON or lacks a value.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public TreadmillTargets? Load()
  {
    if (!File.Exists(_path))
    {
      return null;
    }

    try
    {
      return JsonSerializer.Deserialize<TreadmillTargets>(File.ReadAllText(_path), s_jsonOptions)
        ?? throw new InvalidDataException($"'{_path}' is empty.");
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"'{_path}' is not valid: {exception.Message}", exception);
    }
  }

  /// <summary>
  /// Writes the targets. The file is replaced atomically, so an interrupted save keeps the previous values.
  /// </summary>
  /// <param name="targets">The targets to remember.</param>
  /// <exception cref="IOException">The file cannot be written.</exception>
  /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
  public void Save(TreadmillTargets targets)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    string temporaryPath = _path + ".tmp";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(targets, s_jsonOptions));
    File.Move(temporaryPath, _path, overwrite: true);
  }
}

/// <summary>
/// Target values selected by the user. They are stored as selected; the caller limits them to the range of the
/// connected treadmill.
/// </summary>
/// <param name="SpeedKmh">Target speed in km/h.</param>
/// <param name="InclinePercent">Target incline in percent.</param>
internal sealed record TreadmillTargets(double SpeedKmh, double InclinePercent);
