// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GuanwuAgent.Capabilities;
using GuanwuAgent.Hosting;

namespace GuanwuAgent.Server;

public sealed class AgentServer
{
  public const int Port = 2699;

  private readonly TcpListener _listener = new(IPAddress.Loopback, Port);

  // Kept so shutdown can abort in-flight sessions.
  private readonly ConcurrentDictionary<Session, byte> _sessions = new();

  public AgentIdentity Identity { get; }
  public PathResolver Resolver { get; }
  public CommandRunner Commands { get; } = new();
  public string TempRoot { get; }

  public AgentServer(AgentIdentity identity)
  {
    Identity = identity;
    Resolver = new PathResolver(identity.AgentHome);
    TempRoot = Path.Combine(Path.GetTempPath(), "guanwu-agent");
  }

  public bool TryBind()
  {
    try
    {
      _listener.Start();
      return true;
    }
    catch (SocketException)
    {
      return false;
    }
  }

  public void PrepareHome()
  {
    try
    {
      // Remove leftovers from a previous crash before accepting new jobs.
      if (Directory.Exists(TempRoot))
      {
        Directory.Delete(TempRoot, recursive: true);
      }

      Directory.CreateDirectory(TempRoot);
      NativeMethods.SetPrivateDirectoryMode(TempRoot);
    }
    catch (Exception ex)
    {
      Log.Warn($"could not reset the temp root: {ex.Message}");
    }
  }

  public string CreateJobDirectory()
  {
    var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    var directory = Path.Combine(TempRoot, id);
    Directory.CreateDirectory(directory);
    NativeMethods.SetPrivateDirectoryMode(directory);
    return directory;
  }

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      TcpClient client;
      try
      {
        client = await _listener.AcceptTcpClientAsync(cancellationToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (SocketException ex)
      {
        if (cancellationToken.IsCancellationRequested)
        {
          break;
        }

        Log.Warn($"accept failed: {ex.Message}");
        continue;
      }

      client.NoDelay = true;

      // Session lifetime is independent of the accept loop.
      _ = HandleClientAsync(client, cancellationToken);
    }
  }

  public async Task StopAsync()
  {
    _listener.Stop();

    var sessions = _sessions.Keys.ToArray();
    foreach (var session in sessions)
    {
      await session.AbortAsync();
    }
  }

  private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
  {
    using var connection = client;
    Session? session = null;

    try
    {
      var stream = client.GetStream();
      using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      handshake.CancelAfter(TimeSpan.FromSeconds(30));

      var socket = await WebSocketUpgrade.TryUpgradeAsync(stream, handshake.Token);
      if (socket is null)
      {
        return;
      }

      session = new Session(this, socket);
      _sessions[session] = 0;
      await session.RunAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
      Log.Error($"connection failed: {ex.Message}");
    }
    finally
    {
      if (session is not null)
      {
        _sessions.TryRemove(session, out _);
      }
    }
  }
}
