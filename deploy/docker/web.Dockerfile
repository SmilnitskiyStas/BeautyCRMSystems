# syntax=docker/dockerfile:1
# Frontend (Next.js 16). Контекст = корінь монорепо (npm workspaces: frontend + packages/booking-core).
#   docker build -f deploy/docker/web.Dockerfile \
#     --build-arg NEXT_PUBLIC_API_URL=https://beauty.agrusystems.pp.ua --build-arg NEXT_PUBLIC_TENANT_SLUG=<slug> \
#     -t beautycrm-deploy-web .
# NEXT_PUBLIC_* вбудовуються у збірку: зміна значення = перезбірка образу.
FROM node:22-bookworm-slim AS build
WORKDIR /repo
COPY package.json package-lock.json ./
COPY frontend/package.json frontend/
COPY packages/booking-core/package.json packages/booking-core/
RUN npm ci --workspace=frontend --workspace=@beauty/booking-core --include-workspace-root
COPY packages/ packages/
COPY frontend/ frontend/
ARG NEXT_PUBLIC_API_URL=""
ARG NEXT_PUBLIC_TENANT_SLUG=""
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL \
    NEXT_PUBLIC_TENANT_SLUG=$NEXT_PUBLIC_TENANT_SLUG \
    NEXT_TELEMETRY_DISABLED=1
WORKDIR /repo/frontend
RUN npm run build
WORKDIR /repo
RUN npm prune --omit=dev --workspace=frontend --workspace=@beauty/booking-core --include-workspace-root

FROM node:22-bookworm-slim AS run
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0
WORKDIR /repo
# Зберігаємо розкладку монорепо: next.config.ts вказує turbopack.root на `..`, booking-core — симлінк у node_modules.
COPY --from=build --chown=node:node /repo/package.json ./package.json
COPY --from=build --chown=node:node /repo/node_modules ./node_modules
COPY --from=build --chown=node:node /repo/packages ./packages
COPY --from=build --chown=node:node /repo/frontend ./frontend
USER node
WORKDIR /repo/frontend
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:3000/login').then(r=>process.exit(r.status<500?0:1)).catch(()=>process.exit(1))"
CMD ["node", "../node_modules/next/dist/bin/next", "start", "-p", "3000", "-H", "0.0.0.0"]
