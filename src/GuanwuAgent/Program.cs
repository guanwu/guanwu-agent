// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using GuanwuAgent.Hosting;
using GuanwuAgent.Server;

namespace GuanwuAgent;

public static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var identity = new AgentIdentity();

    // Scheme registration is best-effort; failure must not prevent startup.
    SchemeRegistration.EnsureRegistered();

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
      e.Cancel = true;
      shutdown.Cancel();
    };

    var server = new AgentServer(identity);
    if (!server.TryBind())
    {
      // A second instance is harmless; report success so launchers stay quiet.
      Log.Warn("another agent is already listening on 127.0.0.1:2699");
      return 0;
    }

    server.PrepareHome();
    Log.Info($"{identity} listening on ws://127.0.0.1:2699/");

    try
    {
      await server.RunAsync(shutdown.Token);
    }
    finally
    {
      await server.StopAsync();
    }

    return 0;
  }
}
