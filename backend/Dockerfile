# Beauty CRM API (ASP.NET Core 8). Контекст збірки: каталог backend/.
#   docker build -t beautycrm-api backend
# Міграції в образ не входять: SQL-скрипт (dotnet ef migrations script --idempotent) формує staging-images.yml (ADR-016 §2.8).
# Без секретів: усе через змінні середовища під час запуску.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src
# Спершу лише .csproj: шар restore кешується, поки не змінились залежності.
COPY BeautyCrm.Domain/BeautyCrm.Domain.csproj BeautyCrm.Domain/
COPY BeautyCrm.Application/BeautyCrm.Application.csproj BeautyCrm.Application/
COPY BeautyCrm.Infrastructure/BeautyCrm.Infrastructure.csproj BeautyCrm.Infrastructure/
COPY BeautyCrm.Api/BeautyCrm.Api.csproj BeautyCrm.Api/
RUN dotnet restore BeautyCrm.Api/BeautyCrm.Api.csproj
COPY . .

FROM restore AS publish
# ImageSharp 3.2 перевіряє ліцензію Six Labors під час Release-збірки (ADR-027 §2.5, gate G2).
# Ключ приходить BuildKit-секретом (id=sixlabors_license_key) і живе лише в цьому RUN: у шари й образ не потрапляє.
#   docker build --secret id=sixlabors_license_key,env=SIXLABORS_LICENSE_KEY -t beautycrm-api backend
RUN --mount=type=secret,id=sixlabors_license_key,required=true \
    set -eu; \
    LIC=BeautyCrm.Infrastructure/sixlabors.lic; \
    cp /run/secrets/sixlabors_license_key "$LIC"; \
    test -s "$LIC" || { echo "sixlabors_license_key secret is empty" >&2; rm -f "$LIC"; exit 1; }; \
    dotnet publish BeautyCrm.Api/BeautyCrm.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false \
      || { rm -f "$LIC"; exit 1; }; \
    rm -f "$LIC"; \
    ! find /app/publish -iname 'sixlabors.lic' | grep -q .

# ---- runtime (останній stage = ціль за замовчуванням) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Базовий образ 8.0 має непривілейованого користувача app (UID 1654).
USER 1654
# У образі немає curl: перевірка через bash /dev/tcp (liveness /healthz без залежностей).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "BeautyCrm.Api.dll"]
