// The panel's service worker: what makes it an installable app that opens without the network. The build
// (vite.config.ts, pwaServiceWorker) fills in the files of this version and names the cache after them.
//
// It never touches the API, SignalR or anything that is not a GET of this site: those always go to the
// aggregator, which checks the session on every request. Pages are network first, so an online panel is always
// the deployed version; the cached shell answers only when the network does not. Files under /assets/ have
// content hashes in their names, so a cached copy is never stale.
const VERSION = "__VERSION__";
const FILES = __FILES__;
const CACHE = `cf-${VERSION}`;

self.addEventListener("install", (event) => {
  // Every file of this version, so any screen opens offline, not only those visited before.
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(["/", ...FILES])));
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      // The previous version's files are never asked for again.
      .then((names) => Promise.all(names.filter((name) => name !== CACHE).map((name) => caches.delete(name))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) return;

  event.respondWith(request.mode === "navigate" ? page(request) : file(request));
});

// Every route of the SPA is the same index.html, so one cached shell serves them all.
async function page(request) {
  try {
    const response = await fetch(request);
    if (response.ok) {
      const cache = await caches.open(CACHE);
      await cache.put("/", response.clone());
    }
    return response;
  } catch (error) {
    const cached = await caches.match("/");
    if (cached) return cached;
    throw error;
  }
}

// The files of this version come from the cache; anything else goes to the network as usual.
async function file(request) {
  return (await caches.match(request)) ?? fetch(request);
}
