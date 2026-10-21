// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using GuanwuAgent.Protocol;

namespace GuanwuAgent.Capabilities;

public sealed record HttpRequestSpec(
  string Url,
  string Method,
  IReadOnlyDictionary<string, string[]> Headers,
  int TimeoutMs);

public sealed record HttpResult(
  int Status,
  IReadOnlyDictionary<string, string[]> Headers,
  long Size,
  long DurationMs);

// Per-connection HTTP client and cookie jar. Redirects are followed
// manually so body replay, method changes, and credential stripping stay
// under the agent's control; response bodies are staged to disk.
public sealed class HttpSession : IDisposable
{
  private const int BufferSize = 64 * 1024;

  private readonly HttpClient _client;
  private readonly CookieJar _cookies = new();

  public HttpSession()
  {
    var handler = new SocketsHttpHandler
    {
      UseCookies = false,
      AllowAutoRedirect = false,
      AutomaticDecompression = DecompressionMethods.All,
      UseProxy = true,
    };

    handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
    _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
  }

  public IReadOnlyList<CookieRecord> GetCookies() => _cookies.Snapshot();

  public void SetCookies(IReadOnlyList<CookieRecord> records) => _cookies.Replace(records);

  public async Task<HttpResult> SendAsync(
    HttpRequestSpec spec,
    string bodyPath,
    string responsePath,
    CancellationToken cancellationToken)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    if (spec.TimeoutMs > 0)
    {
      timeout.CancelAfter(spec.TimeoutMs);
    }

    var token = timeout.Token;
    var stopwatch = Stopwatch.StartNew();
    var uri = new Uri(spec.Url, UriKind.Absolute);
    var method = new HttpMethod(spec.Method);
    var bodyLength = new FileInfo(bodyPath).Length;
    var hasBody = bodyLength > 0 || (method != HttpMethod.Get && method != HttpMethod.Head);
    var redirects = 0;

    try
    {
      while (true)
      {
        using var request = new HttpRequestMessage(method, uri);
        if (hasBody)
        {
          AttachBody(request, bodyPath, bodyLength);
        }

        ApplyHeaders(request, spec.Headers, redirects > 0);
        ApplyCookies(request, uri);

        using var response = await _client.SendAsync(
          request,
          HttpCompletionOption.ResponseHeadersRead,
          token);

        StoreCookies(uri, response);

        if (TryGetRedirect(response, uri, method, redirects, out var nextUri, out var nextMethod))
        {
          redirects++;
          uri = nextUri;
          method = nextMethod;
          hasBody = method != HttpMethod.Get && method != HttpMethod.Head;
          continue;
        }

        var headers = CollectHeaders(response);
        var size = await StageBodyAsync(response, responsePath, token);
        stopwatch.Stop();

        return new HttpResult(
          (int)response.StatusCode,
          headers,
          size,
          stopwatch.ElapsedMilliseconds);
      }
    }
    catch (OperationCanceledException)
    {
      if (cancellationToken.IsCancellationRequested)
      {
        throw;
      }

      if (timeout.IsCancellationRequested)
      {
        throw new OperationTimeoutException("HTTP request timed out");
      }

      throw;
    }
    catch (HttpRequestException ex)
    {
      throw new GuanwuException(ex.Message);
    }
    catch (IOException ex)
    {
      throw new GuanwuException(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
      throw new GuanwuException(ex.Message);
    }
  }

  public void Dispose() => _client.Dispose();

  private static void AttachBody(HttpRequestMessage request, string bodyPath, long bodyLength)
  {
    if (bodyLength == 0)
    {
      request.Content = new ByteArrayContent(Array.Empty<byte>());
      return;
    }

    var body = new FileStream(
      bodyPath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      BufferSize,
      FileOptions.Asynchronous | FileOptions.SequentialScan);

    var content = new StreamContent(body, BufferSize);
    content.Headers.ContentLength = bodyLength;
    request.Content = content;
  }

  private static void ApplyHeaders(
    HttpRequestMessage request,
    IReadOnlyDictionary<string, string[]> headers,
    bool isRedirect)
  {
    foreach (var (name, values) in headers)
    {
      if (isRedirect &&
          (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ProxyAuthorization", StringComparison.OrdinalIgnoreCase)))
      {
        // A redirect may target another origin, so credentials are never
        // forwarded on the follow-up request.
        continue;
      }

      // Framing headers stay under the client's control; Cookie is applied
      // after the jar so both sources can be merged.
      if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (request.Headers.TryAddWithoutValidation(name, values))
      {
        continue;
      }

      if (request.Content is not null &&
          request.Content.Headers.TryAddWithoutValidation(name, values))
      {
        continue;
      }

      throw new GuanwuException($"invalid header: {name}");
    }

    if (headers.TryGetValue("Cookie", out var cookieValues))
    {
      foreach (var value in cookieValues)
      {
        request.Headers.TryAddWithoutValidation("Cookie", value);
      }
    }
  }

  private void ApplyCookies(HttpRequestMessage request, Uri uri)
  {
    var fromJar = _cookies.Header(uri);
    if (fromJar is null)
    {
      return;
    }

    var existing = request.Headers.TryGetValues("Cookie", out var values)
      ? string.Join("; ", values)
      : null;

    request.Headers.Remove("Cookie");
    request.Headers.TryAddWithoutValidation(
      "Cookie",
      existing is null ? fromJar : existing + "; " + fromJar);
  }

  private void StoreCookies(Uri uri, HttpResponseMessage response)
  {
    if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
    {
      return;
    }

    foreach (var value in setCookies)
    {
      _cookies.Store(uri, value);
    }
  }

  // Follows a redirect only when the limit is not reached, a Location is
  // present, and the target is HTTP(S); HTTPS never downgrades to HTTP.
  private static bool TryGetRedirect(
    HttpResponseMessage response,
    Uri current,
    HttpMethod currentMethod,
    int redirects,
    out Uri next,
    out HttpMethod method)
  {
    next = current;
    method = currentMethod;

    if (redirects >= Limits.MaxRedirects ||
        response.Headers.Location is not { } location ||
        !IsRedirect(response.StatusCode) ||
        !Uri.TryCreate(current, location, out var candidate))
    {
      return false;
    }

    if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
    {
      return false;
    }

    if (current.Scheme == Uri.UriSchemeHttps && candidate.Scheme == Uri.UriSchemeHttp)
    {
      return false;
    }

    // 303 and POST-over-301/302 continue as body-less GET; every other
    // redirect keeps the method and resends the body.
    method = response.StatusCode switch
    {
      HttpStatusCode.SeeOther when currentMethod != HttpMethod.Head => HttpMethod.Get,
      HttpStatusCode.MovedPermanently or HttpStatusCode.Found when currentMethod == HttpMethod.Post => HttpMethod.Get,
      _ => currentMethod,
    };

    next = candidate;
    return true;
  }

  private static bool IsRedirect(HttpStatusCode status) => status is
    HttpStatusCode.MovedPermanently or
    HttpStatusCode.Found or
    HttpStatusCode.SeeOther or
    HttpStatusCode.TemporaryRedirect or
    HttpStatusCode.PermanentRedirect;

  private static async Task<long> StageBodyAsync(
    HttpResponseMessage response,
    string responsePath,
    CancellationToken cancellationToken)
  {
    await using var output = new FileStream(
      responsePath,
      FileMode.Create,
      FileAccess.Write,
      FileShare.Read,
      BufferSize,
      FileOptions.Asynchronous | FileOptions.SequentialScan);

    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
    await source.CopyToAsync(output, BufferSize, cancellationToken);
    await output.FlushAsync(cancellationToken);

    return output.Length;
  }

  private static IReadOnlyDictionary<string, string[]> CollectHeaders(HttpResponseMessage response)
  {
    var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    AddHeaders(groups, response.Headers);
    AddHeaders(groups, response.Content.Headers);

    var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
    foreach (var (name, values) in groups)
    {
      result[name.ToLowerInvariant()] = values.ToArray();
    }

    return result;
  }

  private static void AddHeaders(
    SortedDictionary<string, List<string>> groups,
    HttpHeaders headers)
  {
    foreach (var (name, values) in headers)
    {
      if (!groups.TryGetValue(name, out var list))
      {
        list = new List<string>();
        groups[name] = list;
      }

      list.AddRange(values);
    }
  }
}
