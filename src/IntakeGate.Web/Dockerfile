FROM node:24.21.0-alpine AS build
WORKDIR /source

COPY package.json package-lock.json ./
RUN npm ci

COPY index.html tsconfig.json tsconfig.app.json tsconfig.node.json vite.config.ts ./
COPY src/ src/
RUN npm run typecheck && npm run build

FROM nginxinc/nginx-unprivileged:1.29.1-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /source/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --retries=12 --start-period=3s \
  CMD wget --quiet --output-document=/dev/null http://127.0.0.1:8080/ui-health || exit 1
