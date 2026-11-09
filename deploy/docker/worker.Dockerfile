# syntax=docker/dockerfile:1
# Worker (Node/TS). Контекст = корінь репозиторію: docker build -f deploy/docker/worker.Dockerfile -t beautycrm-deploy-worker .
# Worker не входить у npm workspaces (свій package-lock). Запуск через tsx (залежність `start` у package.json),
# тому tsx лишається у production-залежностях образу, решта devDependencies (vitest, testcontainers, ...) — ні.
FROM node:22-bookworm-slim AS deps
WORKDIR /app
COPY worker/package.json worker/package-lock.json ./
RUN npm ci --omit=dev && npm install --no-save --no-package-lock tsx@^4.19.0 \
 && npm cache clean --force

FROM node:22-bookworm-slim AS run
ENV NODE_ENV=production
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY worker/package.json worker/tsconfig.json ./
COPY worker/src ./src
# Тести й тестові утиліти в образ не потрібні.
RUN find src -name '*.test.ts' -delete && chown -R node:node /app
USER node
# Процес не слухає порт: «здоров'я» = процес живий (падає на старті при поганій ролі БД / конфігу).
HEALTHCHECK --interval=60s --timeout=5s --start-period=20s --retries=3 \
  CMD node -e "process.exit(0)"
CMD ["node_modules/.bin/tsx", "src/main.ts"]
