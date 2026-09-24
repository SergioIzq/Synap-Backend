# ETAPA 1: BUILD - Compilar la aplicación
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar los archivos de proyecto (sin la solución para no arrastrar los test projects)
COPY Synap.Api/Synap.Api.csproj Synap.Api/
COPY Synap.Application/Synap.Application.csproj Synap.Application/
COPY Synap.Domain/Synap.Domain.csproj Synap.Domain/
COPY Synap.Infrastructure/Synap.Infrastructure.csproj Synap.Infrastructure/
COPY Synap.Shared.Application/Synap.Shared.Application.csproj Synap.Shared.Application/
COPY Synap.Shared.Domain/Synap.Shared.Domain.csproj Synap.Shared.Domain/

# Restaurar dependencias
RUN dotnet restore Synap.Api/Synap.Api.csproj

# Copiar el resto del código fuente
COPY . .

WORKDIR /src/Synap.Api
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ETAPA 2: RUNTIME - Crear la imagen final
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 80

RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/logs && chmod 777 /app/logs

COPY --from=publish /app/publish .

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost/health || exit 1

ENTRYPOINT ["dotnet", "Synap.Api.dll"]
