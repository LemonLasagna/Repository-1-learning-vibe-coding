# syntax=docker/dockerfile:1
# PharmaOS web: the Angular app as static files, behind nginx, which also hands /api, /bff and the health checks to the
# platform host (what the Angular dev server's proxy does in development). Build context: the pharma-platform folder.
#   docker build -f deploy/images/web.Dockerfile -t pharmaos/web:dev .

FROM node:24-slim AS build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npx ng build

FROM nginxinc/nginx-unprivileged:1.28-alpine AS runtime
# The official image fills ${...} in /etc/nginx/templates/*.template from the environment when it starts.
ENV PLATFORM_URL=http://platform:8080 \
    DNS_RESOLVER=127.0.0.11
COPY deploy/images/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/dist/web/browser /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=6 \
  CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/"]
