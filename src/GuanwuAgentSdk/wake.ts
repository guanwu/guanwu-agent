// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

const WakeUrl = "ga://wake";
const FrameLifetimeMs = 2000;

// Starts the registered ga:// handler without navigating away from the page.
// Browsers require user activation for the launch, so the SDK wakes right after
// a dial made from the caller's click fails. Returns false outside a document,
// where there is no handler to launch.
export function wake(): boolean {
  if (typeof document === "undefined") {
    return false;
  }

  const frame = document.createElement("iframe");
  frame.hidden = true;
  frame.src = WakeUrl;
  (document.body ?? document.documentElement).append(frame);
  window.setTimeout(() => frame.remove(), FrameLifetimeMs);
  return true;
}
