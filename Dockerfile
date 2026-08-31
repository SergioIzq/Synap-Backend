# ETAPA 1: BUILD - Compilar la aplicación
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar los archivos de solución y proyectos
COPY Synap.slnx ./
COPY Synap.Api/Synap.Api.csproj Synap.Api/
COPY Synap.Application/Synap.Application.csproj Synap.Application/
COPY Synap.Domain/Synap.Domain.csproj Synap.Domain/
COPY Synap.Infrastructure/Synap.Infrastructure.csproj Synap.Infrastructure/
COPY Synap.Shared.Application/Synap.Shared.Application.csproj Synap.Shared.Application/
COPY Synap.Shared.Domain/Synap.Shared.Domain.csproj Synap.Shared.Domain/

# Restaurar dependencias
RUN dotnet restore

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

RUN mkdir -p /app/logs && chmod 777 /app/logs

COPY --from=publish /app/publish .

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl --fail http://localhost/health || exit 1

ENTRYPOINT ["dotnet", "Synap.Api.dll"]
