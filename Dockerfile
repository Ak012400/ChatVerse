# ─────────────────────────────────────────────────────────────
# ChatVerse.API — multi-stage Docker build for Render / any
# container host. Optimised for cache hits: csproj copies first,
# restore, then full source copy + publish.
# ─────────────────────────────────────────────────────────────

# 1. Build stage — full SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution + every project file so `dotnet restore` can resolve
# the full dependency graph from a clean layer. Source code copy
# happens *after* restore so editing C# doesn't bust the package cache.
COPY ChatVerse.sln ./
COPY ChatVerse.API/ChatVerse.API.csproj          ChatVerse.API/
COPY ChatVerse.Application/ChatVerse.Application.csproj  ChatVerse.Application/
COPY ChatVerse.Domain/ChatVerse.Domain.csproj    ChatVerse.Domain/
COPY ChatVerse.Infrastructure/ChatVerse.Infrastructure.csproj ChatVerse.Infrastructure/
COPY ChatVerse/ChatVerse.csproj                  ChatVerse/

RUN dotnet restore ChatVerse.API/ChatVerse.API.csproj

# Now the actual source.
COPY . .

RUN dotnet publish ChatVerse.API/ChatVerse.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false


# 2. Runtime stage — slim ASP.NET image, no SDK
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Render injects a dynamic PORT; Program.cs reads it via Environment.
# This default is just so `docker run` locally also works.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false

COPY --from=build /app/publish ./

# Document the default port — Render overrides via $PORT.
EXPOSE 10000

ENTRYPOINT ["dotnet", "ChatVerse.API.dll"]
