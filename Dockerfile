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
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MediaLens.Api.dll"]
