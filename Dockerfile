# Build from the repository root:  docker build -t umutledger .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first, so this layer is reused until a package or SDK version changes
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/UmutLedger/UmutLedger.csproj src/UmutLedger/
RUN dotnet restore src/UmutLedger/UmutLedger.csproj

COPY src/UmutLedger/ src/UmutLedger/
RUN dotnet publish src/UmutLedger/UmutLedger.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The base image defines a non-root user and listens on port 8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "UmutLedger.dll"]
