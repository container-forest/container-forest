import { fileURLToPath } from "node:url";
import tailwindcss from "@tailwindcss/vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import react from "@vitejs/plugin-react";
import Icons from "unplugin-icons/vite";
import { defineConfig, type ProxyOptions } from "vite";

// The aggregator's REST port in local development (see CONTRIBUTING.md).
const aggregator = process.env.CF_AGGREGATOR_URL ?? "http://localhost:5080";

// Until the login exists, the aggregator accepts X-Dev-User in Development. Setting CF_DEV_USER makes the
// dev server add it to every proxied request, so the panel acts as that user without any code in the app.
const devUser = process.env.CF_DEV_USER;

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
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: 5173,
    // Same origin as in production, where nginx proxies these paths: cookies stay SameSite=Strict, no CORS.
    proxy: {
      "/api": proxy,
      "/hubs": { ...proxy, ws: true },
    },
  },
});
