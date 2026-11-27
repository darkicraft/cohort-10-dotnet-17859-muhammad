# Docket: Task Management System

A full-stack task manager: ASP.NET Core 10 API, React 19 SPA, EF Core on SQL Server,
Serilog logging, and a test suite built with xUnit, Vitest, and Playwright.

> Status: Week 1 complete (foundation, authentication, web shell).

## Stack

| Layer | Technology |
| --- | --- |
| API | ASP.NET Core 10, controllers, OpenAPI + Scalar |
| Data | EF Core 10, SQL Server 2022 |
| Auth | ASP.NET Core Identity, JWT + refresh tokens |
| Logging | Serilog → console + Seq |
| Backend tests | xUnit, FluentAssertions, NSubstitute, Testcontainers, Respawn |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS 4, TanStack Query |
| Frontend tests | Vitest, Testing Library, Playwright |
| Quality | SonarQube, coverage gates, Impeccable detector |

## Prerequisites

- .NET 10 SDK (`global.json` pins the feature band)
- Node.js 22 or newer
- Docker with Compose v2
- Linux only: `libicu` (Ubuntu: `libicu78`)

## Getting started

```bash
cp .env.example .env          # local values; .env is git-ignored
docker compose up -d          # SQL Server, Seq, Mailpit, SonarQube
dotnet run --project backend/Docket.Api

cd frontend
npm install
npm run dev
```

| Service | URL |
| --- | --- |
| Web app | http://localhost:5173 |
| API | http://localhost:5003 |
| OpenAPI UI | http://localhost:5003/scalar (Development) |
| Health | http://localhost:5003/health |
| Seq | http://localhost:5341 |
| Mailpit | http://localhost:8025 |
| SonarQube | http://localhost:9000 (admin/admin on first run) |

The API runs on the host; SQL Server, Seq, Mailpit, and SonarQube run in containers. The SPA
reads `VITE_API_BASE_URL` (default `http://localhost:5003`); the API allows the dev origin
`http://localhost:5173` through CORS. Register an account in the browser; it is created in
SQL Server and signed in immediately.

Changing `MSSQL_PORT` in `.env` only changes the port Compose publishes on the host. The API
reads `ConnectionStrings:Default`, which targets 1433 by default. If you change the port, set
`ConnectionStrings__Default` in the shell that runs `dotnet run`, using the example in
`.env.example`.

## Build and test

```bash
dotnet build backend/Docket.sln
dotnet test backend/Docket.Tests/Docket.Tests.csproj
./scripts/verify.sh          # what must pass before merging into main

cd frontend
npm run lint
npm run typecheck
npm test
npm run build
```

Windows: `./scripts/verify.ps1` for the backend gate.

## Repository layout

```
backend/
  Docket.Domain/          entities, enums, invariants (no dependencies)
  Docket.Application/     use cases, DTOs, validators, abstractions
  Docket.Infrastructure/  EF Core, Identity, JWT, email, jobs
  Docket.Api/             controllers, middleware, DI, OpenAPI
  Docket.Tests/           unit, integration, architecture, E2E-facing tests
frontend/                React SPA (Vite, Tailwind, TanStack Query)
deploy/                  containers and deployment assets
docs/                    ADRs and design notes
compose.yaml             local SQL Server, Seq, Mailpit, SonarQube
PRODUCT.md               product truth for the design workflow
DESIGN.md                visual system: tokens, components, rules
```

## Design workflow

The web surface is designed with [Impeccable](https://impeccable.style/). `PRODUCT.md` owns
product truth, `DESIGN.md` owns the visual system (tokens, components, do's and don'ts), and
`npm run detect` runs the anti-slop detector in CI. The design direction is "The Task
Ledger": cool paper, ink, one teal stamp, rules instead of cards.

## Roles

Exactly two roles: `Admin` and `User`.

- `User` sees and manages only their own tasks.
- `Admin` sees and manages every task and every user.

## Authentication

| Method | Route | Notes |
| --- | --- | --- |
| `POST` | `/api/v1/auth/register` | Creates a `User`, returns tokens, sets refresh cookie |
| `POST` | `/api/v1/auth/login` | Returns an access token, sets refresh cookie |
| `POST` | `/api/v1/auth/refresh` | Rotates the refresh token; reuse revokes the family |
| `POST` | `/api/v1/auth/logout` | Revokes the refresh token and clears the cookie |
| `GET` | `/api/v1/auth/me` | Requires `Authorization: Bearer <access token>` |
| `GET` | `/api/v1/admin/ping` | Requires the `Admin` role |

Access tokens live 15 minutes and are signed with HS256. Refresh tokens live 7 days, are
stored as SHA-256 hashes, and travel in an `HttpOnly`, `SameSite=Lax` cookie scoped to
`/api/v1/auth`. The development signing key in `appsettings.Development.json` is for local
use only; production must supply `Jwt__SigningKey` (32+ characters) through the environment.

### First admin

A fresh database has the `Admin` and `User` roles, but no users. For the first production
deploy, set these in the deployment environment:

```bash
export Bootstrap__AdminEmail='admin@your-domain'
export Bootstrap__AdminDisplayName='Docket Admin'

read -r -s -p "Admin password: " Bootstrap__AdminPassword
echo
export Bootstrap__AdminPassword
```

Replace the placeholders before running the commands. In an automated deployment, set
`Bootstrap__AdminPassword` from the platform secret store instead of typing it. The prompt
keeps the password out of shell history and prevents `$` characters from expanding.

The API creates the admin once and skips the step on later starts. Sign in and store the
password in your password manager, then remove the three bootstrap variables. A
password-change screen is not part of this slice. If no admin exists and the variables are
missing, production startup fails. Development only logs a warning.

Run the refresh-token family migration with the previous app version stopped. The migration
revokes all legacy active tokens, and the database rejects any token without a family, so
existing sessions sign in once after the upgrade.

## Testing notes

Unit tests and the health smoke test need no Docker. Auth integration tests start a real
SQL Server container with Testcontainers; on a machine without Docker run:

```bash
dotnet test backend/Docket.Tests/Docket.Tests.csproj --filter "Category!=Integration"
```

CI runs the full suite on Linux and unit tests on macOS and Windows.

## Logging

Serilog writes structured logs to the console and to Seq at `http://localhost:5342`
(ingestion). Request logging includes method, path, status, and elapsed time; unhandled
exceptions are logged with a trace id and returned as RFC 7807 `ProblemDetails` without
internal details.

## License

Assignment project. Not licensed for redistribution.
