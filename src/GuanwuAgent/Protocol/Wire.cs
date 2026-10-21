// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Text.Json;

namespace GuanwuAgent.Protocol;

// Text frames carry JSON. A chunk header is always followed by exactly one
// Binary frame; entry streams use JSON Lines.
public static class Wire
{
  public static byte[] BuildChunk(string stream, bool last) => Build(writer =>
  {
    writer.WriteStartObject();
    writer.WriteString("type", "chunk");
    writer.WriteString("stream", stream);
    writer.WriteBoolean("last", last);
    writer.WriteEndObject();
  });

  public static byte[] BuildResult(Action<Utf8JsonWriter> writeResult) => Build(writer =>
  {
    writer.WriteStartObject();
    writer.WriteString("type", "result");
    writer.WritePropertyName("result");
    writer.WriteStartObject();
    writeResult(writer);
    writer.WriteEndObject();
    writer.WriteEndObject();
  });

  public static byte[] BuildError(string code, string message) => Build(writer =>
  {
    writer.WriteStartObject();
    writer.WriteString("type", "error");
    writer.WriteString("code", code);
    writer.WriteString("message", message);
    writer.WriteEndObject();
  });

  public static byte[] BuildJsonLine(Action<Utf8JsonWriter> writeObject) => Build(writer =>
  {
    writer.WriteStartObject();
    writeObject(writer);
    writer.WriteEndObject();
  }, appendNewLine: true);

  private static byte[] Build(Action<Utf8JsonWriter> write, bool appendNewLine = false)
  {
    var buffer = new ArrayBufferWriter<byte>(256);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      write(writer);
    }

    if (!appendNewLine)
    {
      return buffer.WrittenSpan.ToArray();
    }

    var withLine = new byte[buffer.WrittenCount + 1];
    buffer.WrittenSpan.CopyTo(withLine);
    withLine[^1] = (byte)'\n';
    return withLine;
  }
}
