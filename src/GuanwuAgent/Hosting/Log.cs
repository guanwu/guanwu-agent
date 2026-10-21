// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Hosting;

public static class Log
{
  private static readonly object Gate = new();

  public static void Info(string message) => Write("info", message);

  public static void Warn(string message) => Write("warn", message);

  public static void Error(string message) => Write("error", message);

  // Logs go to stderr so they never mix with protocol data.
  private static void Write(string level, string message)
  {
    lock (Gate)
    {
      Console.Error.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}");
    }
  }
}
