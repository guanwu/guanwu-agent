// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace GuanwuAgent.Capabilities;

public sealed record CookieRecord(
  string Name,
  string Value,
  string? Domain,
  string Path,
  bool Secure,
  bool HttpOnly,
  long? ExpiresMs);

// Converts between wire cookie records and System.Net.Cookie. Construction
// through Cookie validates names, values, domains, and dates.
public static class CookieCodec
{
  public static CookieRecord Create(
    string name,
    string value,
    string? domain,
    string path,
    bool secure,
    bool httpOnly,
    long? expiresMs)
  {
    _ = new Cookie(name, value);

    if (string.IsNullOrEmpty(domain))
    {
      domain = null;
    }

    if (string.IsNullOrEmpty(path))
    {
      path = "/";
    }

    if (!path.StartsWith('/'))
    {
      throw new CookieException("cookie path must start with '/'");
    }

    if (expiresMs is long milliseconds)
    {
      _ = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    return new CookieRecord(name, value, domain, path, secure, httpOnly, expiresMs);
  }

  public static void Validate(IReadOnlyList<CookieRecord> records)
  {
    var container = new CookieContainer();
    foreach (var record in records)
    {
      if (record.Domain is null)
      {
        _ = new Cookie(record.Name, record.Value);
        continue;
      }

      container.Add(ToCookie(record));
    }
  }

  public static Cookie ToCookie(CookieRecord record)
  {
    var cookie = new Cookie(record.Name, record.Value)
    {
      Domain = record.Domain ?? string.Empty,
      Path = record.Path,
      Secure = record.Secure,
      HttpOnly = record.HttpOnly,
    };

    if (record.ExpiresMs is long milliseconds)
    {
      cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    return cookie;
  }

  public static CookieRecord FromCookie(Cookie cookie)
  {
    long? expiresMs = null;
    if (cookie.Expires != DateTime.MinValue)
    {
      expiresMs = new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    return new CookieRecord(
      cookie.Name,
      cookie.Value,
      string.IsNullOrEmpty(cookie.Domain) ? null : cookie.Domain,
      string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
      cookie.Secure,
      cookie.HttpOnly,
      expiresMs);
  }

  public static bool IsExpired(CookieRecord record, long nowMs) =>
    record.ExpiresMs is long expiresMs && expiresMs <= nowMs;

  public static bool MatchesHostless(CookieRecord record, Uri uri, long nowMs)
  {
    if (IsExpired(record, nowMs))
    {
      return false;
    }

    if (record.Secure && uri.Scheme != Uri.UriSchemeHttps)
    {
      return false;
    }

    return PathMatches(record.Path, uri.AbsolutePath);
  }

  public static bool PathMatches(string cookiePath, string requestPath)
  {
    if (string.IsNullOrEmpty(cookiePath))
    {
      cookiePath = "/";
    }

    if (requestPath == cookiePath)
    {
      return true;
    }

    if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
    {
      return false;
    }

    return cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/';
  }
}
