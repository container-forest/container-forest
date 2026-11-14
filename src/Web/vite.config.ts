import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { relative } from "node:path";
import { fileURLToPath } from "node:url";
import tailwindcss from "@tailwindcss/vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import react from "@vitejs/plugin-react";
import Icons from "unplugin-icons/vite";
import { defineConfig, type Plugin, type ProxyOptions, searchForWorkspaceRoot } from "vite";

// The aggregator's REST port in local development (see docs/development.md).
const aggregator = process.env.CF_AGGREGATOR_URL ?? "http://localhost:5080";

// In Development the aggregator accepts X-Dev-User next to the session cookie. Setting CF_DEV_USER makes the
// dev server add it to every proxied request, so the panel acts as that user without signing in.
const devUser = process.env.CF_DEV_USER;

// deploy/ at the repository root: the agent enrollment screen bundles deploy/agent/compose.yaml.
const deploy = fileURLToPath(new URL("../../deploy", import.meta.url));

const proxy: ProxyOptions = {
  target: aggregator,
  changeOrigin: true,
  headers: devUser ? { "X-Dev-User": devUser } : undefined,
};

// The PWA service worker (pwa/sw.js) with the list of this build's files, so it can cache all of them at install
// and every screen opens offline. Its cache is named after the list: a new build replaces the old cache.
function pwaServiceWorker(): Plugin {
  const publicDir = fileURLToPath(new URL("./public", import.meta.url));
  return {
    name: "cf-pwa-service-worker",
    apply: "build",
    generateBundle(_, bundle) {
      const publicFiles = readdirSync(publicDir, { recursive: true, withFileTypes: true })
        .filter((entry) => entry.isFile() && !entry.name.startsWith("."))
        .map((entry) => relative(publicDir, `${entry.parentPath}/${entry.name}`));
      const files = [...Object.keys(bundle).filter((name) => name !== "index.html"), ...publicFiles]
        .map((name) => `/${name.replaceAll("\\", "/")}`)
        .sort();
      const version = createHash("sha256").update(files.join("\n")).digest("hex").slice(0, 12);
      const source = readFileSync(new URL("./pwa/sw.js", import.meta.url), "utf8")
        .replace("__VERSION__", version)
        .replace("__FILES__", JSON.stringify(files));
      this.emitFile({ type: "asset", fileName: "sw.js", source });
    },
  };
}

export default defineConfig({
  // The router plugin must come before react(); it writes src/routeTree.gen.ts.
  plugins: [
    tanstackRouter({ target: "react", autoCodeSplitting: true }),
    react(),
    tailwindcss(),
    // Material Symbols as React components, compiled at build time: only the icons in use reach the bundle.
    Icons({ compiler: "jsx", jsx: "react" }),
    pwaServiceWorker(),
  ],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)), "@deploy": deploy },
  },
  server: {
    port: 5173,
    // The dev server serves files only from the project; the deploy files live outside it.
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), deploy] },
    // Same origin as in production, where Caddy proxies these paths: cookies stay SameSite=Strict, no CORS.
    proxy: {
      "/api": proxy,
      "/hubs": { ...proxy, ws: true },
    },
  },
  // `npm run preview` serves the production build (with the service worker) the same way.
  preview: {
    port: 4173,
    proxy: {
      "/api": proxy,
      "/hubs": { ...proxy, ws: true },
    },
  },
});
