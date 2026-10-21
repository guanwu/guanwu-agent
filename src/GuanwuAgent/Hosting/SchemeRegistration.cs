// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Win32;

namespace GuanwuAgent.Hosting;

// Registers ga:// for the current user so a browser can launch the agent.
// The command always tracks the current executable path; failure is non-fatal.
public static class SchemeRegistration
{
  private const string SchemeKey = @"Software\Classes\ga";

  public static void EnsureRegistered()
  {
    if (!OperatingSystem.IsWindows())
    {
      return;
    }

    var executable = Environment.ProcessPath;
    if (executable is null)
    {
      return;
    }

    try
    {
      using var key = Registry.CurrentUser.CreateSubKey(SchemeKey);
      if (key is null)
      {
        return;
      }

      key.SetValue(string.Empty, "URL:Guanwu Agent");
      key.SetValue("URL Protocol", string.Empty);

      using var command = key.CreateSubKey(@"shell\open\command");
      if (command is null)
      {
        return;
      }

      var expected = $"\"{executable}\" \"%1\"";
      if (command.GetValue(string.Empty) as string != expected)
      {
        command.SetValue(string.Empty, expected);
      }
    }
    catch (Exception ex)
    {
      Log.Warn($"scheme registration failed: {ex.Message}");
    }
  }
}
