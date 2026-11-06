using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using UrevoSpacewalkCommander.Configuration;

namespace UrevoSpacewalkCommander.GoogleHealth;

/// <summary>
/// Obtains access tokens for the Google Health API with the OAuth 2.0 flow for installed applications: the
/// system browser shows Google's consent page and redirects to a temporary listener on the loopback interface
/// (PKCE protects the code). The refresh token is kept in a <see cref="TokenStore"/>.
/// </summary>
/// <remarks>
/// Not thread-safe; calls are serialized by <see cref="WorkoutSyncService"/>. While the Google Cloud project is in
/// publishing status "Testing", Google lets refresh tokens expire after 7 days; then the user has to sign in again.
/// </remarks>
internal sealed class GoogleAuthorization
{
  /// <summary>Scope that allows adding exercise data to Google Health (and editing what this app added).</summary>
  public const string ExerciseWriteScope = "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.writeonly";

  /// <summary>Google's authorization endpoint.</summary>
  private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

  /// <summary>Google's token endpoint.</summary>
  private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

  /// <summary>OAuth error code of a revoked or expired refresh token.</summary>
  private const string InvalidGrantError = "invalid_grant";

  /// <summary>Page shown in the browser after the redirect.</summary>
  private const string BrowserResponsePage =
    "<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:sans-serif\">"
    + "<h2>Urevo Spacewalk Commander</h2><p>{0}</p><p>You can close this window.</p></body></html>";

  /// <summary>How long the user has to complete the sign-in in the browser.</summary>
  private static readonly TimeSpan s_browserTimeout = TimeSpan.FromMinutes(5);

  /// <summary>An access token is renewed this long before it expires.</summary>
  private static readonly TimeSpan s_expiryMargin = TimeSpan.FromMinutes(1);

  /// <summary>Format of the token endpoint's responses.</summary>
  private static readonly JsonSerializerOptions s_tokenJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
  };

  private readonly HttpClient _http;
  private readonly GoogleClientCredentials _client;
  private readonly TokenStore _tokenStore;

  /// <summary>The current access token; <see langword="null"/> if none was obtained yet.</summary>
  private string? _accessToken;

  /// <summary>When <see cref="_accessToken"/> expires.</summary>
  private DateTimeOffset _accessTokenExpiry;

  /// <summary>
  /// Creates the authorization for one OAuth client.
  /// </summary>
  /// <param name="http">HTTP client with an explicit timeout.</param>
  /// <param name="client">The user's OAuth client.</param>
  /// <param name="tokenStore">Where the refresh token is kept.</param>
  public GoogleAuthorization(HttpClient http, GoogleClientCredentials client, TokenStore tokenStore)
  {
    _http = http;
    _client = client;
    _tokenStore = tokenStore;
  }

  /// <summary>Gets a value indicating whether a refresh token from an earlier sign-in is stored.</summary>
  public bool HasStoredSignIn => _tokenStore.Load() is not null;

  /// <summary>
  /// Returns a valid access token without user interaction, renewing it with the stored refresh token if needed.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  /// <returns>The token, or <see langword="null"/> if the user has to sign in (again).</returns>
  public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
  {
    if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry - s_expiryMargin)
    {
      return _accessToken;
    }

    string? refreshToken = _tokenStore.Load();
    if (refreshToken is null)
    {
      return null;
    }

    var form = new Dictionary<string, string>
    {
      ["client_id"] = _client.ClientId,
      ["client_secret"] = _client.ClientSecret,
      ["refresh_token"] = refreshToken,
      ["grant_type"] = "refresh_token",
    };
    TokenResponse response = await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    if (response.Error == InvalidGrantError)
    {
      _tokenStore.Delete();
      return null;
    }

    return AcceptTokens(response);
  }

  /// <summary>
  /// Discards the cached access token, so that the next call renews it (after the API rejected it).
  /// </summary>
  public void InvalidateAccessToken()
  {
    _accessToken = null;
  }

  /// <summary>
  /// Signs in interactively in the system browser and stores the refresh token.
  /// </summary>
  /// <param name="cancellationToken">Cancels the sign-in.</param>
  /// <exception cref="TimeoutException">The user did not complete the sign-in in time.</exception>
  /// <exception cref="InvalidOperationException">Google reported an error or the user declined.</exception>
  public async Task SignInAsync(CancellationToken cancellationToken)
  {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
      int port = ((IPEndPoint)listener.LocalEndpoint).Port;
      string redirectUri = $"http://127.0.0.1:{port}/";
      string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
      string state = Base64Url(RandomNumberGenerator.GetBytes(16));
      OpenBrowser(BuildAuthorizationUrl(redirectUri, verifier, state));

      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(s_browserTimeout);
      string code;
      try
      {
        code = await ReceiveCodeAsync(listener, state, timeout.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        throw new TimeoutException("The sign-in in the browser was not completed in time.");
      }

      await ExchangeCodeAsync(code, redirectUri, verifier, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      listener.Stop();
    }
  }

  /// <summary>
  /// Encodes bytes as unpadded base64url, as PKCE requires.
  /// </summary>
  /// <param name="bytes">The bytes.</param>
  /// <returns>The encoded text.</returns>
  private static string Base64Url(byte[] bytes)
  {
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
  }

  /// <summary>
  /// Opens a URL in the user's default browser (never an embedded web view, as Google requires).
  /// </summary>
  /// <param name="url">The URL.</param>
  private static void OpenBrowser(string url)
  {
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
  }

  /// <summary>
  /// Builds the URL of Google's consent page. <c>access_type=offline</c> and <c>prompt=consent</c> make Google
  /// return a refresh token every time.
  /// </summary>
  /// <param name="redirectUri">The loopback redirect.</param>
  /// <param name="verifier">The PKCE verifier.</param>
  /// <param name="state">Random value that ties the redirect to this request.</param>
  /// <returns>The URL.</returns>
  private string BuildAuthorizationUrl(string redirectUri, string verifier, string state)
  {
    string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var query = new Dictionary<string, string>
    {
      ["client_id"] = _client.ClientId,
      ["redirect_uri"] = redirectUri,
      ["response_type"] = "code",
      ["scope"] = ExerciseWriteScope,
      ["code_challenge"] = challenge,
      ["code_challenge_method"] = "S256",
      ["state"] = state,
      ["access_type"] = "offline",
      ["prompt"] = "consent",
    };
    return AuthorizationEndpoint + "?" + string.Join("&", query.Select(
      pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
  }

  /// <summary>
  /// Waits for the browser's redirect and extracts the authorization code. Other requests (favicon, pre-connects)
  /// are answered with 404 and ignored.
  /// </summary>
  /// <param name="listener">The loopback listener.</param>
  /// <param name="expectedState">The state sent with the request.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <returns>The authorization code.</returns>
  /// <exception cref="InvalidOperationException">The user declined or the state does not match.</exception>
  private static async Task<string> ReceiveCodeAsync(
    TcpListener listener,
    string expectedState,
    CancellationToken cancellationToken)
  {
    while (true)
    {
      using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
      using NetworkStream stream = connection.GetStream();
      using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
      string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
      string target = requestLine?.Split(' ') is [_, { } path, ..] ? path : string.Empty;
      int queryStart = target.IndexOf('?');
      var query = HttpUtility.ParseQueryString(queryStart >= 0 ? target[(queryStart + 1)..] : string.Empty);
      if (query["code"] is null && query["error"] is null)
      {
        await WriteResponseAsync(stream, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
        continue;
      }

      bool success = query["code"] is not null && query["state"] == expectedState;
      string message = success ? "Sign-in successful." : "Sign-in failed.";
      await WriteResponseAsync(stream, "200 OK", string.Format(BrowserResponsePage, message), cancellationToken)
        .ConfigureAwait(false);
      return success
        ? query["code"]!
        : throw new InvalidOperationException($"Google sign-in failed: {query["error"] ?? "state mismatch"}.");
    }
  }

  /// <summary>
  /// Answers the browser's request.
  /// </summary>
  /// <param name="stream">The connection.</param>
  /// <param name="status">HTTP status line text.</param>
  /// <param name="html">The page.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  private static async Task WriteResponseAsync(
    NetworkStream stream,
    string status,
    string html,
    CancellationToken cancellationToken)
  {
    byte[] body = Encoding.UTF8.GetBytes(html);
    string header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"
      + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Exchanges the authorization code for tokens and stores the refresh token.
  /// </summary>
  /// <param name="code">The authorization code.</param>
  /// <param name="redirectUri">The redirect used for the code (must match).</param>
  /// <param name="verifier">The PKCE verifier.</param>
  /// <param name="cancellationToken">Cancels the request.</param>
  /// <exception cref="InvalidOperationException">Google rejected the exchange.</exception>
  private async Task ExchangeCodeAsync(
    string code,
    string redirectUri,
    string verifier,
    CancellationToken cancellationToken)
  {
    var form = new Dictionary<string, string>
    {
      ["client_id"] = _client.ClientId,
      ["client_secret"] = _client.ClientSecret,
      ["code"] = code,
      ["code_verifier"] = verifier,
      ["redirect_uri"] = redirectUri,
      ["grant_type"] = "authorization_code",
    };
    TokenResponse response = await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    if (response.Error is not null)
    {
      throw new InvalidOperationException($"Google token exchange failed: {response.Error} {response.ErrorDescription}");
    }

    if (response.RefreshToken is not null)
    {
      _tokenStore.Save(response.RefreshToken);
    }

    AcceptTokens(response);
  }

  /// <summary>
  /// Posts a form to the token endpoint. A timeout is reported as <see cref="TimeoutException"/>.
  /// </summary>
  /// <param name="form">The form fields.</param>
  /// <param name="cancellationToken">Cancels the request.</param>
  /// <returns>The response, which carries <see cref="TokenResponse.Error"/> if Google rejected the request.</returns>
  private async Task<TokenResponse> RequestTokenAsync(
    Dictionary<string, string> form,
    CancellationToken cancellationToken)
  {
    try
    {
      using var content = new FormUrlEncodedContent(form);
      using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, content, cancellationToken)
        .ConfigureAwait(false);
      string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
      return JsonSerializer.Deserialize<TokenResponse>(body, s_tokenJsonOptions)
        ?? throw new InvalidOperationException("Empty response from the Google token endpoint.");
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new TimeoutException("The Google token endpoint did not answer in time.");
    }
  }

  /// <summary>
  /// Caches the access token of a successful token response.
  /// </summary>
  /// <param name="response">The response.</param>
  /// <returns>The access token.</returns>
  /// <exception cref="InvalidOperationException">The response contains no access token.</exception>
  private string AcceptTokens(TokenResponse response)
  {
    _accessToken = response.AccessToken
      ?? throw new InvalidOperationException($"Google token request failed: {response.Error} {response.ErrorDescription}");
    _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
    return _accessToken;
  }

  /// <summary>
  /// Response of the Google token endpoint.
  /// </summary>
  /// <param name="AccessToken">The access token.</param>
  /// <param name="ExpiresIn">Lifetime of the access token in seconds.</param>
  /// <param name="RefreshToken">The refresh token; only returned by the code exchange.</param>
  /// <param name="Error">OAuth error code, if the request failed.</param>
  /// <param name="ErrorDescription">Human-readable error text.</param>
  private sealed record TokenResponse(
    string? AccessToken,
    int ExpiresIn,
    string? RefreshToken,
    string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
