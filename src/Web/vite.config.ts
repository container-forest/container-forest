import { fileURLToPath } from "node:url";
import tailwindcss from "@tailwindcss/vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import react from "@vitejs/plugin-react";
import Icons from "unplugin-icons/vite";
import { defineConfig, type ProxyOptions, searchForWorkspaceRoot } from "vite";

// The aggregator's REST port in local development (see CONTRIBUTING.md).
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

export default defineConfig({
  // The router plugin must come before react(); it writes src/routeTree.gen.ts.
  plugins: [
    tanstackRouter({ target: "react", autoCodeSplitting: true }),
    react(),
    tailwindcss(),
    // Material Symbols as React components, compiled at build time: only the icons in use reach the bundle.
    Icons({ compiler: "jsx", jsx: "react" }),
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
});
