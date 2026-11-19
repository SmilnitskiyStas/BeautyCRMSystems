# Beauty CRM worker (Node + TypeScript через tsx, як `npm start`). Контекст збірки: каталог worker/.
# Без секретів: WORKER_DATABASE_URL, Channels__EncryptionKey тощо передаються під час запуску.
FROM node:22-bookworm-slim AS deps
WORKDIR /app
COPY package.json package-lock.json ./
# Залежності для запуску разом з tsx (він у devDependencies, тому --omit=dev не використовуємо);
# тестові інструменти лишаються, але образ не містить тестів і Docker-сокета.
RUN npm ci --no-audit --no-fund

FROM node:22-bookworm-slim AS final
ENV NODE_ENV=production
WORKDIR /app
COPY --from=deps --chown=node:node /app/node_modules ./node_modules
COPY --chown=node:node package.json package-lock.json tsconfig.json ./
COPY --chown=node:node src ./src
USER node
# Лише процес: worker не слухає порт, тож HEALTHCHECK через compose не потрібен (restart policy).
# Те саме, що `npm start` (tsx src/main.ts), але без npm як PID 1 (SIGTERM іде напряму в процес).
CMD ["node_modules/.bin/tsx", "src/main.ts"]
