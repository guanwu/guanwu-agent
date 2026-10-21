// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;

namespace GuanwuAgent.Protocol;

public enum ClientMessageKind
{
  Request,
  Chunk,
  Cancel,
}

// Parses Text frames. Malformed frames close the connection; a request with
// bad fields instead yields request.invalid after it is dispatched.
public sealed class ClientMessage
{
  public ClientMessageKind Kind { get; private init; }
  public string Op { get; private init; } = string.Empty;
  public bool RequestValid { get; private init; } = true;
  public JsonElement Params { get; private init; }
  public bool Last { get; private init; }

  public static bool TryParse(ReadOnlyMemory<byte> utf8, out ClientMessage? message, out string? malformed)
  {
    message = null;
    malformed = null;

    try
    {
      using var document = JsonDocument.Parse(utf8);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object ||
          !root.TryGetProperty("type", out var typeElement) ||
          typeElement.ValueKind != JsonValueKind.String)
      {
        malformed = "message must be an object with a string type";
        return false;
      }

      switch (typeElement.GetString())
      {
        case "request":
        {
          var valid = true;
          var op = string.Empty;
          if (!root.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
          {
            valid = false;
          }
          else
          {
            op = opElement.GetString() ?? string.Empty;
          }

          var parameters = default(JsonElement);
          if (root.TryGetProperty("params", out var paramsElement))
          {
            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
              valid = false;
            }
            else
            {
              parameters = paramsElement.Clone();
            }
          }

          message = new ClientMessage
          {
            Kind = ClientMessageKind.Request,
            Op = op,
            RequestValid = valid,
            Params = parameters,
          };
          return true;
        }

        case "chunk":
          if (!root.TryGetProperty("last", out var lastElement) ||
              lastElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
          {
            malformed = "chunk.last must be a boolean";
            return false;
          }

          message = new ClientMessage
          {
            Kind = ClientMessageKind.Chunk,
            Last = lastElement.GetBoolean(),
          };
          return true;

        case "cancel":
          message = new ClientMessage { Kind = ClientMessageKind.Cancel };
          return true;

        default:
          malformed = "unknown message type";
          return false;
      }
    }
    catch (JsonException ex)
    {
      malformed = ex.Message;
      return false;
    }
  }
}
