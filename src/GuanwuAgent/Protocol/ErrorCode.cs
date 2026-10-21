// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Protocol;

public static class ErrorCodes
{
  public const string RequestInvalid = "request.invalid";
  public const string JobCancelled = "job.cancelled";
  public const string JobTimeout = "job.timeout";
  public const string JobFailed = "job.failed";
  public const string AgentInternal = "agent.internal";
}
