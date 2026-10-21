// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace GuanwuAgent.Hosting;

internal static partial class NativeMethods
{
  // 0700: job files may contain credentials or uploaded data.
  private const uint UserReadWriteExecute = 0x1C0;

  [LibraryImport("libc", EntryPoint = "chmod", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
  private static partial int Chmod(string path, uint mode);

  public static void SetPrivateDirectoryMode(string path)
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    Chmod(path, UserReadWriteExecute);
  }
}
