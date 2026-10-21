// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace GuanwuAgent.Capabilities;

// Domain cookies live in CookieContainer. Cookies without a domain are kept
// separately because CookieContainer cannot represent them.
public sealed class CookieJar
{
  private CookieContainer _container = new();
  private List<CookieRecord> _hostless = new();

  public void Replace(IReadOnlyList<CookieRecord> records)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var container = new CookieContainer();
    var hostless = new List<CookieRecord>();

    foreach (var record in records)
    {
      if (CookieCodec.IsExpired(record, now))
      {
        continue;
      }

      if (record.Domain is null)
      {
        hostless.Add(record);
        continue;
      }

      container.Add(CookieCodec.ToCookie(record));
    }

    _container = container;
    _hostless = hostless;
  }

  public string? Header(Uri uri)
  {
    var parts = new List<string>();
    var header = _container.GetCookieHeader(uri);
    if (!string.IsNullOrEmpty(header))
    {
      parts.Add(header);
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    foreach (var record in _hostless)
    {
      if (CookieCodec.MatchesHostless(record, uri, now))
      {
        parts.Add($"{record.Name}={record.Value}");
      }
    }

    return parts.Count == 0 ? null : string.Join("; ", parts);
  }

  public void Store(Uri uri, string setCookieHeader)
  {
    try
    {
      _container.SetCookies(uri, setCookieHeader);
    }
    catch (CookieException)
    {
    }
  }

  public IReadOnlyList<CookieRecord> Snapshot()
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var records = new List<CookieRecord>();

    foreach (Cookie cookie in _container.GetAllCookies())
    {
      var record = CookieCodec.FromCookie(cookie);
      if (!CookieCodec.IsExpired(record, now))
      {
        records.Add(record);
      }
    }

    foreach (var record in _hostless)
    {
      if (!CookieCodec.IsExpired(record, now))
      {
        records.Add(record);
      }
    }

    return records;
  }
}
