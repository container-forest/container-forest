# Build context: src/Web. The generated API types and route tree are committed, so the aggregator's
# sources are not needed here.
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# Static files only: no Node runtime in production. Caddy serves them and is the single entry point of the
# system (ADR-0005): it proxies /api and /hubs to the aggregator, so the panel and the API share one origin
# (SameSite=Strict cookie, WebSocket without CORS), and gRPC for agents and the bot; with a domain it gets the
# TLS certificate itself.
FROM caddy:2-alpine
LABEL org.opencontainers.image.title="container-forest-web" \
      org.opencontainers.image.source="https://github.com/lepterion/container-forest"
COPY Caddyfile /etc/caddy/Caddyfile
COPY --from=build /app/dist /srv
# Plain HTTP on 8080 unless CF_SITE_ADDRESS names a domain (then 80 and 443); variables and defaults are in the Caddyfile.
EXPOSE 8080 80 443 443/udp
