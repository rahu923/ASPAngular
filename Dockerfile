# syntax=docker/dockerfile:1
ARG DOTNET_SDK=10.0-noble
ARG DOTNET_RUNTIME=10.0-noble
FROM node:22.18.0-bookworm-slim AS frontend
# Dependencies are copied before source so unchanged npm inputs can reuse Docker's build cache.
WORKDIR /ui
COPY frontend/package*.json ./
RUN npm ci --no-fund --no-audit
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /source
COPY global.json ./
COPY src/OrderManagement.Domain/OrderManagement.Domain.csproj src/OrderManagement.Domain/
COPY src/OrderManagement.Application/OrderManagement.Application.csproj src/OrderManagement.Application/
COPY src/OrderManagement.Infrastructure/OrderManagement.Infrastructure.csproj src/OrderManagement.Infrastructure/
COPY src/OrderManagement.Api/OrderManagement.Api.csproj src/OrderManagement.Api/
RUN dotnet restore src/OrderManagement.Api/OrderManagement.Api.csproj
COPY src/ src/
COPY scripts/sql/Logging.sql scripts/sql/Logging.sql
RUN dotnet publish src/OrderManagement.Api/OrderManagement.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME} AS final
# Multi-stage copies bring only published outputs into the runtime; SDK/Node stay in build stages.
RUN groupadd --gid 10001 orders && useradd --uid 10001 --gid orders --no-create-home orders
WORKDIR /app
COPY --from=build /publish/ ./
COPY --from=frontend /ui/dist/orders/browser/ ./wwwroot/
RUN mkdir -p /app/logs /app/local && chown -R orders:orders /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Observability__File__Path=/app/logs/api-.json
EXPOSE 8080
USER 10001:10001
# A compromised application should not have root privileges inside its container.
ENTRYPOINT ["dotnet", "OrderManagement.Api.dll"]
