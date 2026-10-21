// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

import type { ChunkHandler, InputSource, Invoke } from "./connection";
import type { EmptyResult } from "./files";

export interface ExecParams {
  file: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
  timeoutMs?: number;
}

export interface ExecIo {
  input?: InputSource;
  onStdout?: ChunkHandler;
  onStderr?: ChunkHandler;
}

export interface ExecResult {
  exitCode: number;
  durationMs: number;
}

// stdout and stderr are independent streams; input goes to the process stdin.
export class ExecApi {
  constructor(private readonly invoke: Invoke) {
  }

  run(params: ExecParams, io: ExecIo = {}): Promise<ExecResult> {
    return this.invoke<ExecResult>(
      "exec.run",
      params,
      { onStdout: io.onStdout, onStderr: io.onStderr },
      io.input,
    );
  }
}

export type { EmptyResult };
