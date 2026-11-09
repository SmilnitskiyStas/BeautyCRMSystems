# syntax=docker/dockerfile:1
# Backend Beauty CRM (ASP.NET Core, net8.0). Контекст збірки = корінь репозиторію:
#   docker build -f deploy/docker/backend.Dockerfile --target api     -t beautycrm-deploy-api .
#   docker build -f deploy/docker/backend.Dockerfile --target migrate -t beautycrm-deploy-migrate .
# Цілі: `api` (рантайм застосунку) і `migrate` (одноразовий job: idempotent SQL із EF-міграцій + psql, без dotnet-ef на проді).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Спершу лише csproj (кеш restore). Тестовий проєкт у контекст не входить (.dockerignore), тому solution не використовуємо.
COPY backend/BeautyCrm.Domain/BeautyCrm.Domain.csproj backend/BeautyCrm.Domain/
COPY backend/BeautyCrm.Application/BeautyCrm.Application.csproj backend/BeautyCrm.Application/
COPY backend/BeautyCrm.Infrastructure/BeautyCrm.Infrastructure.csproj backend/BeautyCrm.Infrastructure/
COPY backend/BeautyCrm.Api/BeautyCrm.Api.csproj backend/BeautyCrm.Api/
RUN dotnet restore backend/BeautyCrm.Api/BeautyCrm.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/BeautyCrm.Api/BeautyCrm.Api.csproj -c Release --no-restore -o /out/api /p:UseAppHost=false

# Idempotent-скрипт усіх міграцій (включно з вручну написаними SQL-міграціями: вони теж класи Migration).
# dotnet-ef ставиться лише тут, у build-стадії; `script` не підключається до БД.
FROM build AS migrations-script
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 8.0.* \
 && dotnet ef migrations script --idempotent \
      --project backend/BeautyCrm.Infrastructure --startup-project backend/BeautyCrm.Infrastructure \
      --output /out/migrate.sql \
 && test -s /out/migrate.sql

# --- migrate: psql + SQL-скрипти; запускається під роллю-власником схеми ---
FROM postgres:16-alpine AS migrate
COPY --from=migrations-script /out/migrate.sql /opt/migrate/migrate.sql
COPY deploy/db/post-migrate.sql /opt/migrate/post-migrate.sql
COPY deploy/db/run-migrate.sh /opt/migrate/run-migrate.sh
RUN chmod 0555 /opt/migrate/run-migrate.sh
USER postgres
ENTRYPOINT ["/opt/migrate/run-migrate.sh"]

# --- api: рантайм (останній стадій = ціль за замовчуванням) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
# curl потрібен лише для HEALTHCHECK (в образі aspnet його немає).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/api/ ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Образ aspnet:8.0 містить непривілейованого користувача `app` (UID 1654).
USER app
# /health у застосунку немає; публічний resolve неіснуючого коду = 404 not_found, але лише якщо API живий і БД доступна.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/api/public/resolve/XXXXXXXX)" = "404" ] || exit 1
ENTRYPOINT ["dotnet", "BeautyCrm.Api.dll"]
