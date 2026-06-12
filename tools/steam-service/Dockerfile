# Built with the repo root as context (see docker-compose.yml) so the repo-level
# build props and code-style rules apply inside the image build too.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props CodeStyle.props .editorconfig ./
COPY tools/steam-service/*.csproj ./
RUN dotnet restore

COPY tools/steam-service/ .
RUN dotnet publish -c Release -o /app
    # -p:EnableCompressionInSingleFile=true \
    # -p:PublishSingleFile=true \
    # -p:PublishTrimmed=true \
    # -p:PublishAot=true \

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app .

ENV PORT=3001
ENV SESSION_DIR=/data/steam-session
ENV GAME_DIR=/data/game

EXPOSE 3001

# Healthcheck using dotnet to invoke a simple HTTP request (no curl/wget needed)
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD dotnet SteamService.dll healthcheck || exit 1

# Default command is "serve" (HTTP API), but can be overridden:
#   docker run ... setup    # Interactive login + download
#   docker run ... login    # Just login
#   docker run ... download # Download game
#   docker run ... ticket   # Output app ticket
ENTRYPOINT ["dotnet", "SteamService.dll"]
CMD ["serve"]
