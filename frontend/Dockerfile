# Beauty CRM web (Next.js 16, standalone). Контекст збірки: каталог frontend/.
#   docker build -t beautycrm-web --build-arg NEXT_PUBLIC_API_URL=https://api.example.com frontend
# NEXT_PUBLIC_* вшиваються в бандл ПІД ЧАС збірки (не секрети: вони видимі в браузері). Серверну адресу API
# (API_URL, напр. http://api:8080 усередині мережі compose) передавайте під час запуску.
FROM node:22-bookworm-slim AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM node:22-bookworm-slim AS build
WORKDIR /app
ARG NEXT_PUBLIC_API_URL
ARG NEXT_PUBLIC_TENANT_SLUG
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL \
    NEXT_PUBLIC_TENANT_SLUG=$NEXT_PUBLIC_TENANT_SLUG \
    NEXT_OUTPUT=standalone \
    NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:22-bookworm-slim AS final
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    HOSTNAME=0.0.0.0 \
    PORT=3000
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
COPY --from=build --chown=node:node /app/public ./public
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:3000/login').then(r=>process.exit(r.status<500?0:1)).catch(()=>process.exit(1))"]
CMD ["node", "server.js"]
