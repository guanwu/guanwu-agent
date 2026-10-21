// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Protocol;

public enum Op
{
  ExecRun,
  FsList,
  FsRead,
  FsWrite,
  FsDelete,
  FsStat,
  HttpRequest,
  HttpCookiesGet,
  HttpCookiesSet,
}

public static class Ops
{
  public static bool TryParse(string name, out Op op)
  {
    switch (name)
    {
      case "exec.run": op = Op.ExecRun; return true;
      case "fs.list": op = Op.FsList; return true;
      case "fs.read": op = Op.FsRead; return true;
      case "fs.write": op = Op.FsWrite; return true;
      case "fs.delete": op = Op.FsDelete; return true;
      case "fs.stat": op = Op.FsStat; return true;
      case "http.request": op = Op.HttpRequest; return true;
      case "http.cookies.get": op = Op.HttpCookiesGet; return true;
      case "http.cookies.set": op = Op.HttpCookiesSet; return true;
      default: op = default; return false;
    }
  }

  public static string Name(Op op) => op switch
  {
    Op.ExecRun => "exec.run",
    Op.FsList => "fs.list",
    Op.FsRead => "fs.read",
    Op.FsWrite => "fs.write",
    Op.FsDelete => "fs.delete",
    Op.FsStat => "fs.stat",
    Op.HttpRequest => "http.request",
    Op.HttpCookiesGet => "http.cookies.get",
    Op.HttpCookiesSet => "http.cookies.set",
    _ => throw new ArgumentOutOfRangeException(nameof(op)),
  };
}
