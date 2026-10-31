// The panel as an installable app, without React: the service worker, and whether and how this browser can install
// the panel. The browser offers installation once, early, through `beforeinstallprompt`; the event is kept here so
// a button can use it later.

/** Chromium's install prompt; not in the DOM typings. */
interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

/**
 * - `installed`: the panel runs as the installed app;
 * - `available`: the browser lets a button install it;
 * - `ios`: Safari on iPhone or iPad, which installs only through Share → Add to Home Screen;
 * - `unsupported`: the browser's own menu, if anything (Firefox, or Chromium that already offered and was refused).
 */
export type InstallState = "installed" | "available" | "ios" | "unsupported";

let prompt: InstallPromptEvent | null = null;
let installed = false;
const listeners = new Set<() => void>();
const notify = () => listeners.forEach((listener) => listener());

function isStandalone(): boolean {
  return (
    window.matchMedia("(display-mode: standalone)").matches ||
    // Safari on iOS.
    (navigator as Navigator & { standalone?: boolean }).standalone === true
  );
}

function isIosSafari(): boolean {
  const ios = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  return ios && !/CriOS|FxiOS|EdgiOS/.test(navigator.userAgent);
}

export function installState(): InstallState {
  if (installed || isStandalone()) return "installed";
  if (prompt) return "available";
  return isIosSafari() ? "ios" : "unsupported";
}

export function subscribeInstallState(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Shows the browser's install dialog. Resolves to whether the panel was installed. */
export async function install(): Promise<boolean> {
  const event = prompt;
  if (!event) return false;
  // The event can be used once, whatever the answer.
  prompt = null;
  await event.prompt();
  const { outcome } = await event.userChoice;
  notify();
  return outcome === "accepted";
}

/**
 * Starts listening before React renders, since the browser fires `beforeinstallprompt` once, early, and registers
 * the service worker in a production build (the dev server's modules must not be cached).
 */
export function setUpApp() {
  window.addEventListener("beforeinstallprompt", (event) => {
    // Without this Chromium shows its own mini-infobar on phones; the panel offers a button instead.
    event.preventDefault();
    prompt = event as InstallPromptEvent;
    notify();
  });
  window.addEventListener("appinstalled", () => {
    installed = true;
    prompt = null;
    notify();
  });

  if (import.meta.env.PROD && "serviceWorker" in navigator) {
    window.addEventListener("load", () => {
      // Not being installable is no reason for the panel to fail; the reason is in the console.
      navigator.serviceWorker.register("/sw.js").catch((error: unknown) => console.warn("Service worker not registered", error));
    });
  }
}
