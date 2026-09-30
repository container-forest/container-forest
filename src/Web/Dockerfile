# Build context: src/Web. The generated API types and route tree are committed, so the aggregator's
# sources are not needed here.
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# Static files only: no Node runtime in production. nginx proxies /api and /hubs to the aggregator,
# so the panel and the API share one origin (SameSite=Strict cookie, WebSocket without CORS).
FROM nginx:1.29-alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
# The official image fills the template from the environment at start.
ENV AGGREGATOR_URL=http://aggregator:5080
EXPOSE 8080
