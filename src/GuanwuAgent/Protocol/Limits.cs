// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Protocol;

public static class Limits
{
  // Any single Text message above this size closes with 1009.
  public const int MaxTextMessageBytes = 1024 * 1024;
  public const int MaxRedirects = 50;
  public const int DefaultHttpTimeoutMs = 60_000;
  public const int MaxHttpHeaderBytes = 64 * 1024;
}
