# Vue UI (JS; built once on the build host)
FROM --platform=$BUILDPLATFORM node:22-alpine AS ui
WORKDIR /src
COPY datalog-ui/package.json datalog-ui/package-lock.json ./
RUN npm ci
COPY datalog-ui/ ./
RUN npm run build

# .NET publish (cross-compile to TARGETARCH)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY DataLogXE.slnx Directory.Build.props global.json ./
COPY DataLogLib/DataLogLib.csproj DataLogLib/
COPY S7CommPlus/S7CommPlus.csproj S7CommPlus/
COPY DataLogService/DataLogService.csproj DataLogService/
COPY DataLogApi/DataLogApi.csproj DataLogApi/
RUN dotnet restore DataLogApi/DataLogApi.csproj -a $TARGETARCH
COPY DataLogLib/ DataLogLib/
COPY S7CommPlus/ S7CommPlus/
COPY DataLogService/ DataLogService/
COPY DataLogApi/ DataLogApi/
RUN dotnet publish DataLogApi/DataLogApi.csproj -c Release -a $TARGETARCH -o /app --no-restore

# Runtime: API + Vue in one process
# Ubuntu 24.04 glibc: writable /app (Serilog), OPC UA cert stores.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG VERSION=0.0.90
LABEL org.opencontainers.image.title="DataLogXE"
LABEL org.opencontainers.image.version=$VERSION
WORKDIR /app
COPY --from=build /app .
COPY --from=ui /src/dist ./wwwroot
# License texts of the S7CommPlus driver (LGPL) and of the third-party components, linked from the About dialog
COPY S7CommPlus/LICENSE ./wwwroot/LICENSE-LGPL-3.0.txt
COPY THIRD-PARTY-NOTICES.txt ./wwwroot/THIRD-PARTY-NOTICES.txt
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
# The process runs as "app" (see docker-entrypoint.sh), so its log, data and certificates live in folders it owns.
# /app/data is the one volume of the app: master.key, the OPC UA certificates, the XML import work folder and the
# log files, which so outlive an update and go into the support bundle.
ENV LOG_PATH=/app/data/logs
ENV DATA_PATH=/app/data
ENV OPCUA_CERT_PATH=/app/data/opcua
# The database service of the compose file; the app builds its connection from POSTGRES_PASSWORD
ENV POSTGRES_HOST=timescaledb
EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh", "dotnet", "DataLogApi.dll"]
