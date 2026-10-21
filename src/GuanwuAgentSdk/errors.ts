// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

export type AgentErrorCode =
  | "request.invalid"
  | "job.cancelled"
  | "job.timeout"
  | "job.failed"
  | "agent.internal";

// An error reported by the agent; code is one of the protocol error codes.
export class AgentError extends Error {
  readonly code: AgentErrorCode | string;

  constructor(code: AgentErrorCode | string, message: string) {
    super(message);
    this.name = "AgentError";
    this.code = code;
  }
}

// The socket closed or the handshake failed before an operation could run.
export class ConnectionClosedError extends Error {
  constructor(message = "the agent connection is closed") {
    super(message);
    this.name = "ConnectionClosedError";
  }
}

// The wire protocol was violated; the connection is no longer usable.
export class ProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ProtocolError";
  }
}
