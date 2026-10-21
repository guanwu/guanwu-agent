// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

import { Connection } from "./connection";
import type { Invoke } from "./connection";
import { ExecApi } from "./exec";
import { FilesApi } from "./files";
import { HttpApi } from "./http";

export interface ConnectOptions {
  url?: string;
  timeoutMs?: number;
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
    const connection = await Connection.connect(options.url, options.timeoutMs);
    return new AgentClient(connection);
  }

  close(): void {
    this.connection.close();
  }
}

export function connect(options: ConnectOptions = {}): Promise<AgentClient> {
  return AgentClient.connect(options);
}
