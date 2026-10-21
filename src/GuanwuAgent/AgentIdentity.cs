// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using GuanwuAgent.Hosting;

namespace GuanwuAgent;

public sealed class AgentIdentity
{
  public const string Name = "GuanwuAgent";
  public const string Version = "1.0.0";

  // Relative paths and the default working directory are anchored here,
  // not at the process working directory.
  public string AgentHome { get; }
  public string Platform { get; }

  public AgentIdentity()
  {
    var executable = Environment.ProcessPath;
    AgentHome = executable is null
      ? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
      : Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
    Platform = RuntimeInformation.RuntimeIdentifier;
  }

  public override string ToString() => $"{Name} {Version} ({Platform})";
}
