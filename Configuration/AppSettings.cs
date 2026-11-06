using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpacewalkCommander.Configuration;

/// <summary>
/// User settings from <c>%APPDATA%\SpacewalkCommander\settings.json</c>. The Google OAuth client is personal and never
/// part of the source code: it is either entered in <c>settings.json</c> or provided as the
/// <c>client_secret_*.json</c> file downloaded from the Google Cloud console into the same folder.
/// </summary>
internal sealed class AppSettings
{
  /// <summary>Name of the application's folders below AppData.</summary>
  private const string ApplicationFolderName = "SpacewalkCommander";

  /// <summary>Name of the settings file.</summary>
  private const string SettingsFileName = "settings.json";

  /// <summary>Pattern of the OAuth client file downloaded from the Google Cloud console.</summary>
  private const string ClientSecretFilePattern = "client_secret*.json";

  /// <summary>Settings key: body weight in kg for the energy estimate.</summary>
  private const string BodyWeightKey = "bodyWeightKg";

  /// <summary>Settings key: OAuth client id.</summary>
  private const string ClientIdKey = "googleClientId";

  /// <summary>Settings key: OAuth client secret.</summary>
  private const string ClientSecretKey = "googleClientSecret";

  /// <summary>Content written when no settings file exists yet.</summary>
  private const string SettingsTemplate = """
    {
      "bodyWeightKg": null,
      "googleClientId": "",
      "googleClientSecret": ""
    }
    """;

  /// <summary>
  /// Initializes the settings.
  /// </summary>
  /// <param name="bodyWeightKg">Body weight, or <see langword="null"/> if not configured.</param>
  /// <param name="googleClient">OAuth client, or <see langword="null"/> if not configured.</param>
  private AppSettings(double? bodyWeightKg, GoogleClientCredentials? googleClient)
  {
    BodyWeightKg = bodyWeightKg;
    GoogleClient = googleClient;
  }

  /// <summary>Gets the folder with the settings and the OAuth client file (roaming AppData).</summary>
  public static string SettingsDirectory { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    ApplicationFolderName);

  /// <summary>Gets the folder with the workout archive and the stored Google token (local AppData).</summary>
  public static string DataDirectory { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ApplicationFolderName);

  /// <summary>
  /// Gets the body weight in kg used to estimate energy when the treadmill does not report it;
  /// <see langword="null"/> disables the estimate.
  /// </summary>
  public double? BodyWeightKg { get; }

  /// <summary>Gets the Google OAuth client; <see langword="null"/> if Google Health is not set up.</summary>
  public GoogleClientCredentials? GoogleClient { get; }

  /// <summary>
  /// Loads the settings, creating a template settings file on first use.
  /// </summary>
  /// <returns>The settings.</returns>
  /// <exception cref="InvalidDataException">A settings file is not valid JSON.</exception>
  public static AppSettings Load()
  {
    Directory.CreateDirectory(SettingsDirectory);
    string settingsPath = Path.Combine(SettingsDirectory, SettingsFileName);
    if (!File.Exists(settingsPath))
    {
      File.WriteAllText(settingsPath, SettingsTemplate);
    }

    JsonNode? settings = ParseFile(settingsPath);
    double? weight = settings?[BodyWeightKey]?.GetValueKind() == JsonValueKind.Number
      ? settings[BodyWeightKey]!.GetValue<double>()
      : null;
    GoogleClientCredentials? client = CreateCredentials(
      settings?[ClientIdKey]?.ToString(),
      settings?[ClientSecretKey]?.ToString()) ?? LoadDownloadedClient();
    return new AppSettings(weight is > 0 ? weight : null, client);
  }

  /// <summary>
  /// Reads the OAuth client from a <c>client_secret_*.json</c> file of the Google Cloud console.
  /// </summary>
  /// <returns>The client, or <see langword="null"/> if there is no such file.</returns>
  private static GoogleClientCredentials? LoadDownloadedClient()
  {
    string? path = Directory.GetFiles(SettingsDirectory, ClientSecretFilePattern).FirstOrDefault();
    JsonNode? root = path is null ? null : ParseFile(path);
    JsonNode? client = root?["installed"] ?? root?["web"];
    return CreateCredentials(client?["client_id"]?.ToString(), client?["client_secret"]?.ToString());
  }

  /// <summary>
  /// Creates the client credentials if both parts are present.
  /// </summary>
  /// <param name="clientId">The client id.</param>
  /// <param name="clientSecret">The client secret.</param>
  /// <returns>The credentials, or <see langword="null"/> if a part is missing.</returns>
  private static GoogleClientCredentials? CreateCredentials(string? clientId, string? clientSecret)
  {
    return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
      ? null
      : new GoogleClientCredentials(clientId.Trim(), clientSecret.Trim());
  }

  /// <summary>
  /// Parses a JSON file.
  /// </summary>
  /// <param name="path">The file.</param>
  /// <returns>The root node.</returns>
  /// <exception cref="InvalidDataException">The file is not valid JSON.</exception>
  private static JsonNode? ParseFile(string path)
  {
    try
    {
      return JsonNode.Parse(File.ReadAllText(path));
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"'{path}' is not valid JSON: {exception.Message}", exception);
    }
  }
}

/// <summary>
/// The OAuth client of the user's own Google Cloud project (type "Desktop app").
/// </summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">
/// The client secret. For desktop clients Google does not treat it as confidential, but it must still not be
/// committed to source control.
/// </param>
internal sealed record GoogleClientCredentials(string ClientId, string ClientSecret);
