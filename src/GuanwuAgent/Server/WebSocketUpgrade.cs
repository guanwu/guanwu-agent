// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace GuanwuAgent.Server;

// The agent performs the HTTP upgrade itself so it can require the exact
// path and subprotocol without an HTTP server stack.
public static class WebSocketUpgrade
{
  private const int MaxHeaderBytes = 16 * 1024;
  private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

  public static async Task<WebSocket?> TryUpgradeAsync(
    Stream stream,
    CancellationToken cancellationToken)
  {
    // Read byte by byte until CRLFCRLF so no byte of the following WebSocket
    // frame is consumed; a header over 16 KiB is rejected.
    var header = new byte[MaxHeaderBytes];
    var length = 0;

    while (true)
    {
      var read = await stream.ReadAsync(header.AsMemory(length, 1), cancellationToken);
      if (read == 0)
      {
        return null;
      }

      length++;
      if (length >= 4 &&
          header[length - 4] == '\r' &&
          header[length - 3] == '\n' &&
          header[length - 2] == '\r' &&
          header[length - 1] == '\n')
      {
        break;
      }

      if (length == header.Length)
      {
        await WriteBadRequestAsync(stream, cancellationToken);
        return null;
      }
    }

    var text = Encoding.ASCII.GetString(header, 0, length);
    var lines = text.Split("\r\n", StringSplitOptions.None);
    var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (requestLine.Length != 3 ||
        requestLine[0] != "GET" ||
        requestLine[1] != "/" ||
        requestLine[2] != "HTTP/1.1")
    {
      await WriteBadRequestAsync(stream, cancellationToken);
      return null;
    }

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines.Skip(1))
    {
      if (line.Length == 0)
      {
        continue;
      }

      var separator = line.IndexOf(':');
      if (separator <= 0)
      {
        await WriteBadRequestAsync(stream, cancellationToken);
        return null;
      }

      var name = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      headers[name] = headers.TryGetValue(name, out var existing)
        ? existing + ", " + value
        : value;
    }

    // A missing or different subprotocol is a bad request, not a fallback.
    if (!HasToken(headers, "Upgrade", "websocket") ||
        !HasToken(headers, "Connection", "Upgrade") ||
        !headers.TryGetValue("Sec-WebSocket-Version", out var version) ||
        version != "13" ||
        !headers.TryGetValue("Sec-WebSocket-Key", out var key) ||
        !OffersSubprotocol(headers))
    {
      await WriteBadRequestAsync(stream, cancellationToken);
      return null;
    }

    var accept = Convert.ToBase64String(
      SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    var response = string.Concat(
      "HTTP/1.1 101 Switching Protocols\r\n",
      "Upgrade: websocket\r\n",
      "Connection: Upgrade\r\n",
      "Sec-WebSocket-Accept: ", accept, "\r\n",
      "Sec-WebSocket-Protocol: ga.v1\r\n",
      "\r\n");

    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
    await stream.FlushAsync(cancellationToken);

    return WebSocket.CreateFromStream(
      stream,
      new WebSocketCreationOptions
      {
        IsServer = true,
        SubProtocol = "ga.v1",
        KeepAliveInterval = TimeSpan.Zero,
      });
  }

  private static bool OffersSubprotocol(Dictionary<string, string> headers)
  {
    if (!headers.TryGetValue("Sec-WebSocket-Protocol", out var offered))
    {
      return false;
    }

    foreach (var protocol in offered.Split(','))
    {
      if (protocol.Trim() == "ga.v1")
      {
        return true;
      }
    }

    return false;
  }

  private static bool HasToken(Dictionary<string, string> headers, string name, string token)
  {
    if (!headers.TryGetValue(name, out var value))
    {
      return false;
    }

    foreach (var part in value.Split(','))
    {
      if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  private static async Task WriteBadRequestAsync(Stream stream, CancellationToken cancellationToken)
  {
    const string response =
      "HTTP/1.1 400 Bad Request\r\n" +
      "Content-Length: 0\r\n" +
      "Connection: close\r\n" +
      "\r\n";

    try
    {
      await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
      await stream.FlushAsync(cancellationToken);
    }
    catch (Exception)
    {
    }
  }
}
