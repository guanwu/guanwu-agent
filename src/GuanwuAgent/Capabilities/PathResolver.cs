// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

namespace GuanwuAgent.Capabilities;

// Resolves relative paths against AgentHome. Bare executable names are
// searched in AgentHome first, then in PATH.
public sealed class PathResolver
{
  public string AgentHome { get; }

  public PathResolver(string agentHome)
  {
    AgentHome = agentHome;
  }

  public string Resolve(string path)
  {
    try
    {
      return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AgentHome, path));
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      throw new GuanwuException($"{path}: {ex.Message}");
    }
  }

  public string ResolveWorkingDirectory(string? path) =>
    string.IsNullOrEmpty(path) ? AgentHome : Resolve(path);

  public string ResolveExecutable(string file)
  {
    if (string.IsNullOrEmpty(file))
    {
      throw new GuanwuException("executable name is empty");
    }

    if (file.Contains(Path.DirectorySeparatorChar) || file.Contains(Path.AltDirectorySeparatorChar))
    {
      var explicitPath = Resolve(file);
      if (File.Exists(explicitPath))
      {
        return explicitPath;
      }

      throw new GuanwuException($"{file}: executable not found");
    }

    foreach (var directory in SearchDirectories())
    {
      foreach (var candidate in CandidateNames(file))
      {
        var path = Path.Combine(directory, candidate);
        if (File.Exists(path))
        {
          return path;
        }
      }
    }

    throw new GuanwuException($"{file}: executable not found");
  }

  private IEnumerable<string> SearchDirectories()
  {
    yield return AgentHome;

    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(path))
    {
      yield break;
    }

    foreach (var directory in path.Split(Path.PathSeparator))
    {
      if (!string.IsNullOrWhiteSpace(directory))
      {
        yield return directory;
      }
    }
  }

  private static IEnumerable<string> CandidateNames(string file)
  {
    yield return file;

    if (!OperatingSystem.IsWindows() || file.Contains('.'))
    {
      yield break;
    }

    yield return file + ".exe";
    yield return file + ".com";
  }
}
