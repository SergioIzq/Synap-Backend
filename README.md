# Synap.Backend

API de [Synap](https://github.com/SergioIzq/Synap-Workspace), el "segundo cerebro" personal de Sergio: captura de notas/snippets/enlaces, búsqueda y un asistente con IA que responde preguntas ancladas en tu propio contenido.

Las specs, el diseño técnico y las tareas de este proyecto viven en el repo [Synap-Workspace](https://github.com/SergioIzq/Synap-Workspace) (OpenSpec), no aquí — este repo es solo el código.

## Stack

- .NET 10, DDD + Arquitectura Hexagonal + CQRS (MediatR)
- PostgreSQL + `pgvector` (via Npgsql)
- Paquetes propios reutilizados: `SergioIzq.Domain.Kernel`, `SergioIzq.Application.Kernel`, `SergioIzq.AspNetCore.Kernel` (parcialmente — ver `design.md` del change `synap-mvp` para el porqué de qué partes sí y cuáles no)

## Estructura de la solución

Misma organización que [Kash-Backend](https://github.com/SergioIzq/Kash-Backend):

```
Synap.Api             → Controllers, autenticación, Program.cs
Synap.Application     → MediatR: Features/<Capability>/Commands|Queries/<UseCase>
Synap.Domain          → Agregados, entidades, eventos de dominio, contratos de repositorio
Synap.Infrastructure  → EF Core (Postgres), repositorios Read/Write, servicios
Synap.Shared.Domain   → Kernel de dominio compartido
Synap.Shared.Application → Kernel de aplicación compartido
```

A diferencia de Kash, aquí **no** se usa `SergioIzq.Infrastructure.Kernel` (es MySQL-only por diseño) — el `DbContext` base, el `IUnitOfWork` y los repositorios Read/Write están implementados directamente contra Postgres.

## Desarrollo local

```bash
dotnet restore
dotnet build
dotnet run --project Synap.Api
```

Necesita una cadena de conexión a Postgres en `Synap.Api/appsettings.Development.json` (`ConnectionStrings:DefaultConnection`) o levantar la base con el `docker-compose.yml` del [workspace](https://github.com/SergioIzq/Synap-Workspace).

## Servicio de IA

`ai-service/` es un servicio FastAPI (Python) independiente, desplegado aparte, que gestiona embeddings y el chat RAG. Ver su propio `Dockerfile`.
