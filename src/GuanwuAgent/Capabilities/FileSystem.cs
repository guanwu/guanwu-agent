// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;

namespace GuanwuAgent.Capabilities;

public enum FsEntryType
{
  File,
  Dir,
  Link,
  Other,
}

public sealed record FsEntry(string Name, string Path, FsEntryType Type, long? Size, long MtimeMs);

public sealed record FsStat(FsEntryType Type, long Size, long MtimeMs);

// File operations work on already-resolved absolute paths. Links are
// reported and deleted as links; only stat follows them to the final target.
public static class FileSystem
{
  private const int BufferSize = 64 * 1024;

  public static FileStream CreateFile(string path) => new(
    path,
    FileMode.CreateNew,
    FileAccess.Write,
    FileShare.None,
    BufferSize,
    FileOptions.Asynchronous | FileOptions.SequentialScan);

  public static FileStream OpenRead(string path) => new(
    path,
    FileMode.Open,
    FileAccess.Read,
    FileShare.Read,
    BufferSize,
    FileOptions.Asynchronous | FileOptions.SequentialScan);

  // The part file is created in the target directory so the commit rename
  // stays on one volume and replaces the target atomically.
  public static FileStream CreatePartFile(string targetPath, out string partPath)
  {
    var directory = Path.GetDirectoryName(targetPath);
    if (string.IsNullOrEmpty(directory))
    {
      throw new GuanwuException($"{targetPath}: target directory is missing");
    }

    for (var attempt = 0; attempt < 5; attempt++)
    {
      var candidate = Path.Combine(
        directory,
        ".gw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".part");

      try
      {
        var stream = new FileStream(
          candidate,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          BufferSize,
          FileOptions.Asynchronous | FileOptions.SequentialScan);

        partPath = candidate;
        return stream;
      }
      catch (IOException) when (File.Exists(candidate))
      {
      }
    }

    partPath = string.Empty;
    throw new GuanwuException($"{targetPath}: could not create a temporary file");
  }

  // A directory junction cannot be overwritten by a file rename, so it is
  // removed first; the link target itself is never touched.
  public static void Commit(string partPath, string targetPath)
  {
    try
    {
      var attributes = File.GetAttributes(targetPath);
      if ((attributes & FileAttributes.ReparsePoint) != 0 &&
          (attributes & FileAttributes.Directory) != 0)
      {
        Directory.Delete(targetPath, recursive: false);
      }
    }
    catch (FileNotFoundException)
    {
    }
    catch (DirectoryNotFoundException)
    {
    }

    File.Move(partPath, targetPath, overwrite: true);
  }

  public static void TryDeleteFile(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (Exception)
    {
    }
  }

  // Stat follows links to describe the final target, unlike List and Delete.
  public static FsStat Stat(string path)
  {
    if (!File.Exists(path) && !Directory.Exists(path))
    {
      throw new FileNotFoundException($"{path}: not found", path);
    }

    var attributes = File.GetAttributes(path);
    if ((attributes & FileAttributes.ReparsePoint) != 0)
    {
      var target = ResolveLinkTarget(path, attributes);
      if (target is not null)
      {
        path = target;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
          throw new FileNotFoundException($"{path}: not found", path);
        }

        attributes = File.GetAttributes(path);
      }
    }

    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds();

    if ((attributes & FileAttributes.Directory) != 0)
    {
      return new FsStat(FsEntryType.Dir, 0, mtime);
    }

    if ((attributes & FileAttributes.Device) != 0)
    {
      return new FsStat(FsEntryType.Other, 0, mtime);
    }

    long size;
    try
    {
      size = new FileInfo(path).Length;
    }
    catch (IOException)
    {
      size = 0;
    }

    return new FsStat(FsEntryType.File, size, mtime);
  }

  private static string? ResolveLinkTarget(string path, FileAttributes attributes)
  {
    try
    {
      var target = (attributes & FileAttributes.Directory) != 0
        ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
        : File.ResolveLinkTarget(path, returnFinalTarget: true);
      return target?.FullName;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return null;
    }
  }

  // Recursion follows real directories only; links are listed but never
  // entered, so a link cycle cannot trap the walk.
  public static IEnumerable<FsEntry> List(string path, bool recursive)
  {
    var root = Path.GetFullPath(path);
    if (!Directory.Exists(root))
    {
      throw new DirectoryNotFoundException($"{path}: not found");
    }

    var pending = new Stack<string>();
    pending.Push(root);

    while (pending.Count > 0)
    {
      var directory = pending.Pop();
      foreach (var child in Directory.EnumerateFileSystemEntries(directory))
      {
        var entry = Describe(child);
        yield return entry;

        if (recursive && entry.Type == FsEntryType.Dir)
        {
          pending.Push(child);
        }
      }
    }
  }

  // A link is removed itself, never followed; recursive deletion applies to
  // real directories and the links they contain.
  public static void Delete(string path, bool recursive)
  {
    var attributes = File.GetAttributes(path);
    var isLink = (attributes & FileAttributes.ReparsePoint) != 0;

    if (isLink)
    {
      DeleteLink(path, attributes);
      return;
    }

    if ((attributes & FileAttributes.Directory) == 0)
    {
      File.Delete(path);
      return;
    }

    if (!recursive)
    {
      Directory.Delete(path, recursive: false);
      return;
    }

    foreach (var child in Directory.EnumerateFileSystemEntries(path))
    {
      DeleteRecursive(child);
    }

    Directory.Delete(path, recursive: false);
  }

  private static void DeleteRecursive(string path)
  {
    var attributes = File.GetAttributes(path);
    if ((attributes & FileAttributes.ReparsePoint) != 0)
    {
      DeleteLink(path, attributes);
      return;
    }

    if ((attributes & FileAttributes.Directory) != 0)
    {
      foreach (var child in Directory.EnumerateFileSystemEntries(path))
      {
        DeleteRecursive(child);
      }

      Directory.Delete(path, recursive: false);
      return;
    }

    File.Delete(path);
  }

  private static void DeleteLink(string path, FileAttributes attributes)
  {
    if ((attributes & FileAttributes.Directory) != 0)
    {
      Directory.Delete(path, recursive: false);
    }
    else
    {
      File.Delete(path);
    }
  }

  private static FsEntry Describe(string path)
  {
    var attributes = File.GetAttributes(path);
    var name = Path.GetFileName(path);
    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds();

    if ((attributes & FileAttributes.ReparsePoint) != 0)
    {
      return new FsEntry(name, path, FsEntryType.Link, null, mtime);
    }

    if ((attributes & FileAttributes.Directory) != 0)
    {
      return new FsEntry(name, path, FsEntryType.Dir, 0, mtime);
    }

    if ((attributes & FileAttributes.Device) != 0)
    {
      return new FsEntry(name, path, FsEntryType.Other, 0, mtime);
    }

    long size;
    try
    {
      size = new FileInfo(path).Length;
    }
    catch (IOException)
    {
      return new FsEntry(name, path, FsEntryType.Other, 0, mtime);
    }

    return new FsEntry(name, path, FsEntryType.File, size, mtime);
  }
}
