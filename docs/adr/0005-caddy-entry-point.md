# ADR-0005. The single entry point is Caddy in the panel's image

- Status: accepted
- Date: 2026-10-02

## Context

Deploying the system on a server needs:

1. TLS for everything coming from outside: the browser, agents and the Discord bot. Agents connect from other hosts, often from behind NAT, so gRPC goes over the internet too.
2. One origin for the panel and its API (`/api`, `/hubs`): `SameSite=Strict` cookies and WebSocket without CORS depend on it.
3. Long gRPC streams from agents and the bot that run through the proxy for hours without timing out.

Until then nginx in the `container-forest-web` image served the panel. It proxied `/api` and `/hubs`, but:

- certificates need a separate certbot and its renewals;
- gRPC needs `grpc_pass` and manual timeouts, since the default 60 s break agent streams;
- `add_header` in a `location` cancels `server`-level headers, so CSP and `nosniff` had to be repeated in every block; a missed line silently dropped a security header.

## Decision

The `container-forest-web` image is built on Caddy (`src/Web/Caddyfile`) and is the single entry point to the system:

- it serves the SPA's files: `index.html` with `Cache-Control: no-cache`, `/assets/*` forever, client routes get `index.html`, and a missing asset is a 404;
- it proxies `/api` and `/hubs` to the aggregator's REST port;
- it proxies every request with `Content-Type: application/grpc*` (agents and the bot) to the aggregator's gRPC port as HTTP/2 without TLS (`h2c`), without buffering or timeouts;
- with a domain in `CF_SITE_ADDRESS`, Caddy obtains and renews a Let's Encrypt certificate itself (ports 80 and 443); `:8080` (the default) is plain HTTP for local runs or behind another proxy that terminates TLS; h2c is on there, so agents work that way too.

The aggregator publishes no ports and trusts `X-Forwarded-*` (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`): it can only be reached through Caddy. The deployment example is `deploy/aggregator/compose.yaml`; agents and the bot connect to `https://<domain>`.

## Rejected options

- **Keep nginx** (as both the edge and the SPA server): it works, but certificates, gRPC and headers have to be configured by hand, and that's exactly where mistakes are most dangerous.
- **Traefik:** strong at discovering containers by labels, but for that it needs the Docker socket (root on the host), and its configuration is more complex. We have three services, and a static config is enough.
- **A separate edge proxy in front of the panel's nginx:** another container for no gain; Caddy can serve files anyway.
- **Serving the SPA from the aggregator:** contradicts the decision that the panel is a separate service (ADR-0001).

## Consequences

- nginx disappears from the stack; security headers are set once for all static files.
- TLS in the aggregator itself (running without a proxy) remains possible, but the typical deployment doesn't need it.
- Caddy keeps certificates and the ACME account in the `caddy-data` volume: without it every restart goes to Let's Encrypt again, which has rate limits.
- Checked live: `docker compose` with the aggregator and panel images, sign-in through Caddy, an agent in a container enrolled and connected to the aggregator through Caddy over h2c, and the container list went through the whole chain.
