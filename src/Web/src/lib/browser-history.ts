import { createBrowserHistory } from "@tanstack/react-router";

/**
 * The browser history for the router, robust against extensions that patch `history.pushState` and `replaceState`.
 *
 * TanStack's history (`@tanstack/history`, up to at least 1.170) writes the URL with an unbound call:
 * `(push ? win.history.pushState : win.history.replaceState)(state, "", href)`. That works while the methods are
 * its own wrappers, which ignore `this`. A browser extension that patches them again later and calls the native
 * method with `this` (seen in Firefox as `patchedHistoryMethod`) then throws "'replaceState' called on an object
 * that does not implement interface History": the URL stays where it was, so the address bar showed "/" and Back
 * closed the tab.
 *
 * The router gets a window whose `history` hands out every method already bound to the real history, whatever is
 * installed at that moment: the native method, the router's wrapper or an extension's. Assignments still go to the
 * real history, so the router's wrappers are installed there as before.
 */
export function createRobustBrowserHistory() {
  return createBrowserHistory({ window: withBoundHistory(window) });
}

function withBoundHistory(win: Window): Window {
  const history = new Proxy(win.history, {
    // Read on the real object: getters such as `state` and `length` refuse any other `this`.
    get: (target, key) => bound(target, Reflect.get(target, key, target)),
  });

  return new Proxy(win, {
    get: (target, key) => (key === "history" ? history : bound(target, Reflect.get(target, key, target))),
  });
}

function bound(target: object, value: unknown): unknown {
  return typeof value === "function" ? (value as (...args: unknown[]) => unknown).bind(target) : value;
}
