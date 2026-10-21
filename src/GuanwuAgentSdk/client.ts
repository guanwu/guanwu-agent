// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

import { Connection, DefaultConnectTimeoutMs } from "./connection";
import type { Invoke } from "./connection";
import { ConnectionClosedError } from "./errors";
import { ExecApi } from "./exec";
import { FilesApi } from "./files";
import { HttpApi } from "./http";
import { ENDPOINT } from "./protocol";
import { wake } from "./wake";

const WakeAttemptTimeoutMs = 1000;
const WakeRetryDelayMs = 250;
const WakeAttempts = 40;

export interface ConnectOptions {
  url?: string;
  // Bounds each connection attempt.
  timeoutMs?: number;
  // Start the agent through its ga:// handler when a dial fails. Defaults to true.
  wake?: boolean;
}

// Public SDK facade. All APIs share one connection, and Connection serialises
// their operations.
export class AgentClient {
  readonly exec: ExecApi;
  readonly fs: FilesApi;
  readonly http: HttpApi;

  private constructor(private readonly connection: Connection) {
    const invoke: Invoke = <T>(
      op: string,
      params: object,
      handlers: Parameters<Invoke>[2],
      input?: Parameters<Invoke>[3],
    ) => this.connection.run<T>(op, params, handlers, input);

    this.exec = new ExecApi(invoke);
    this.fs = new FilesApi(invoke);
    this.http = new HttpApi(invoke);
  }

  static async connect(options: ConnectOptions = {}): Promise<AgentClient> {
    const connection = await connectWithWake(options);
    return new AgentClient(connection);
  }

  close(): void {
    this.connection.close();
  }
}

export function connect(options: ConnectOptions = {}): Promise<AgentClient> {
  return AgentClient.connect(options);
}

// A failed dial to the default endpoint wakes the agent and retries. A running
// agent connects before any wake, so it never opens the browser's permission
// dialog. The retry count bounds the wait because a browser pauses the page
// while that dialog is open. Only the default endpoint can be woken; another
// URL belongs to a server this page did not start.
async function connectWithWake(options: ConnectOptions): Promise<Connection> {
  const url = options.url ?? ENDPOINT;
  const timeoutMs = options.timeoutMs ?? DefaultConnectTimeoutMs;
  const wakes = url === ENDPOINT && options.wake !== false;
  const attemptTimeoutMs = wakes ? Math.min(timeoutMs, WakeAttemptTimeoutMs) : timeoutMs;

  try {
    return await Connection.connect(url, attemptTimeoutMs);
  } catch (error) {
    if (!wakes || !(error instanceof ConnectionClosedError)) {
      throw error;
    }
  }

  wake();
  let lastError: ConnectionClosedError | null = null;

  for (let attempt = 0; attempt < WakeAttempts; attempt++) {
    try {
      return await Connection.connect(url, attemptTimeoutMs);
    } catch (error) {
      if (!(error instanceof ConnectionClosedError)) {
        throw error;
      }

      lastError = error;
    }

    await delay(WakeRetryDelayMs);
  }

  throw lastError ?? new ConnectionClosedError("timed out while connecting to the agent");
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
