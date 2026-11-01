FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/MediaLens.Core/MediaLens.Core.csproj src/MediaLens.Core/
COPY src/MediaLens.Api/MediaLens.Api.csproj src/MediaLens.Api/
RUN dotnet restore src/MediaLens.Api/MediaLens.Api.csproj
COPY src/ src/
RUN dotnet publish src/MediaLens.Api/MediaLens.Api.csproj \
    --configuration Release --no-restore --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY LICENSE ./LICENSE
# Runtime settings are injected with docker run --env / --env-file:
#   SECRET_TOKEN: required; 32-256 ASCII characters using Bearer token syntax.
#   HTTP_PORT: container-internal HTTP port; defaults to 8080.
#   VALIDATE_PUBLIC_IP: true by default; false skips public IP checks for diagnostics.
#     Host, scheme, and port restrictions still apply. Recreate the container
#     after changing its environment.
# SECRET_TOKEN has no image default; the application rejects missing or invalid values.
ENV HTTP_PORT=8080
# EXPOSE documents the default; publish the selected container port with -p / ports.
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MediaLens.Api.dll"]
