using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrevoSpacewalkCommander.GoogleHealth;

/// <summary>
/// Calls the Google Health API v4.
/// </summary>
internal sealed class GoogleHealthClient
{
  /// <summary>Base URL of the stable API channel.</summary>
  private const string BaseUrl = "https://health.googleapis.com/v4/";

  /// <summary>Collection of the user's exercise sessions.</summary>
  private const string ExerciseDataPointsPath = "users/me/dataTypes/exercise/dataPoints";

  /// <summary>Identity of the signed-in user; fails if the account is not linked to Google Health.</summary>
  private const string IdentityPath = "users/me/identity";

  /// <summary>Error reason when the Google account has never signed in to the Google Health app.</summary>
  private const string AccountNotLinkedReason = "ACCOUNT_NOT_LINKED";

  /// <summary>JSON format of the API: camelCase, absent values omitted.</summary>
  private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  private readonly HttpClient _http;

  /// <summary>
  /// Creates the client.
  /// </summary>
  /// <param name="http">HTTP client with an explicit timeout.</param>
  public GoogleHealthClient(HttpClient http)
  {
    _http = http;
  }

  /// <summary>
  /// Verifies that the signed-in account can use Google Health.
  /// </summary>
  /// <param name="accessToken">A valid access token.</param>
  /// <param name="cancellationToken">Cancels the request.</param>
  /// <exception cref="GoogleHealthException">The API rejected the request.</exception>
  public async Task CheckIdentityAsync(string accessToken, CancellationToken cancellationToken)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + IdentityPath);
    using HttpResponseMessage response = await SendAsync(request, accessToken, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Creates an exercise session.
  /// </summary>
  /// <param name="dataPoint">The session.</param>
  /// <param name="accessToken">A valid access token.</param>
  /// <param name="cancellationToken">Cancels the request.</param>
  /// <returns>The operation; <see cref="OperationDto.Done"/> is <see langword="false"/> while still processing.</returns>
  /// <exception cref="GoogleHealthException">The API rejected the session.</exception>
  public async Task<OperationDto> CreateExerciseAsync(
    ExerciseDataPointDto dataPoint,
    string accessToken,
    CancellationToken cancellationToken)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ExerciseDataPointsPath)
    {
      Content = JsonContent.Create(dataPoint, options: s_jsonOptions),
    };
    using HttpResponseMessage response = await SendAsync(request, accessToken, cancellationToken).ConfigureAwait(false);
    OperationDto operation = await response.Content
      .ReadFromJsonAsync<OperationDto>(s_jsonOptions, cancellationToken)
      .ConfigureAwait(false) ?? new OperationDto(null, true, null);
    if (operation.Error is { } error)
    {
      throw new GoogleHealthException(
        (HttpStatusCode)error.Code,
        $"Google Health rejected the workout: {error.Message}",
        isAccountNotLinked: false);
    }

    return operation;
  }

  /// <summary>
  /// Serializes a data point the way it is sent; used for diagnostics and tests.
  /// </summary>
  /// <param name="dataPoint">The data point.</param>
  /// <returns>The JSON text.</returns>
  public static string Serialize(ExerciseDataPointDto dataPoint)
  {
    return JsonSerializer.Serialize(dataPoint, s_jsonOptions);
  }

  /// <summary>
  /// Sends an authorized request and turns error responses into <see cref="GoogleHealthException"/>.
  /// </summary>
  /// <param name="request">The request.</param>
  /// <param name="accessToken">A valid access token.</param>
  /// <param name="cancellationToken">Cancels the request.</param>
  /// <returns>The successful response; the caller disposes it.</returns>
  private async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    string accessToken,
    CancellationToken cancellationToken)
  {
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    HttpResponseMessage response;
    try
    {
      response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new TimeoutException("The Google Health API did not answer in time.");
    }

    if (response.IsSuccessStatusCode)
    {
      return response;
    }

    using (response)
    {
      string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
      throw new GoogleHealthException(
        response.StatusCode,
        $"Google Health API: {(int)response.StatusCode} {ExtractMessage(body)}",
        body.Contains(AccountNotLinkedReason, StringComparison.Ordinal));
    }
  }

  /// <summary>
  /// Extracts the error message of a Google API error body.
  /// </summary>
  /// <param name="body">The response body.</param>
  /// <returns>The message, or the raw body if it is not a Google error.</returns>
  private static string ExtractMessage(string body)
  {
    try
    {
      return JsonSerializer.Deserialize<ErrorResponseDto>(body, s_jsonOptions)?.Error?.Message ?? body;
    }
    catch (JsonException)
    {
      return body;
    }
  }
}

/// <summary>
/// The Google Health API rejected a request.
/// </summary>
internal sealed class GoogleHealthException : Exception
{
  /// <summary>
  /// Creates the exception.
  /// </summary>
  /// <param name="statusCode">The HTTP status.</param>
  /// <param name="message">Developer-facing description.</param>
  /// <param name="isAccountNotLinked">Whether the account has never been set up in the Google Health app.</param>
  public GoogleHealthException(HttpStatusCode statusCode, string message, bool isAccountNotLinked)
    : base(message)
  {
    StatusCode = statusCode;
    IsAccountNotLinked = isAccountNotLinked;
  }

  /// <summary>Gets the HTTP status of the response.</summary>
  public HttpStatusCode StatusCode { get; }

  /// <summary>Gets a value indicating whether the access token was rejected (expired or revoked).</summary>
  public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

  /// <summary>
  /// Gets a value indicating whether the Google account is not linked to Google Health: the user has to sign in
  /// to the Google Health app on the phone once.
  /// </summary>
  public bool IsAccountNotLinked { get; }
}
