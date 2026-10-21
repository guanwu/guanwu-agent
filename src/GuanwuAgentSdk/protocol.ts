// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

export const ENDPOINT = "ws://127.0.0.1:2699/";
export const SUBPROTOCOL = "ga.v1";

// Text messages are JSON; each chunk Text message is followed by exactly one
// Binary frame carrying that stream's bytes.
export interface ClientRequest {
  type: "request";
  op: string;
  params: object;
}

export interface ClientChunk {
  type: "chunk";
  last: boolean;
}

export interface ClientCancel {
  type: "cancel";
}

export type ClientText = ClientRequest | ClientChunk | ClientCancel;

export interface ServerChunk {
  type: "chunk";
  stream: string;
  last: boolean;
}

export interface ServerResult {
  type: "result";
  result: unknown;
}

export interface ServerError {
  type: "error";
  code: string;
  message: string;
}

export type ServerText = ServerChunk | ServerResult | ServerError;

export type FsEntryType = "file" | "dir" | "link" | "other";

export interface FsEntry {
  name: string;
  path: string;
  type: FsEntryType;
  size?: number;
  mtimeMs: number;
}

export interface CookieInfo {
  name: string;
  value: string;
  domain?: string;
  path: string;
  secure: boolean;
  httpOnly: boolean;
  expiresMs?: number;
}
