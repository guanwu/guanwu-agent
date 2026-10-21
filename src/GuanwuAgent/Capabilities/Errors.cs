// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Capabilities;

// Capability failures are reported as job.failed.
public class GuanwuException : Exception
{
  public GuanwuException(string message) : base(message)
  {
  }
}

// Timeouts have their own error code so callers can distinguish them from
// ordinary failures.
public sealed class OperationTimeoutException : Exception
{
  public OperationTimeoutException(string message) : base(message)
  {
  }
}
