FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

COPY ProxyNetworker.Core/ProxyNetworker.Core.csproj ProxyNetworker.Core/
COPY ProxyNetworker.Server/ProxyNetworker.Server.csproj ProxyNetworker.Server/
RUN dotnet restore ProxyNetworker.Server/ProxyNetworker.Server.csproj

COPY ProxyNetworker.Core/ ProxyNetworker.Core/
COPY ProxyNetworker.Server/ ProxyNetworker.Server/

RUN dotnet publish ProxyNetworker.Server/ProxyNetworker.Server.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o /app/publish

FROM ubuntu:20.04 AS openssl11

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends libssl1.1 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends libnuma1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=openssl11 /usr/lib/x86_64-linux-gnu/libcrypto.so.1.1 /app/

ENV LD_LIBRARY_PATH=/app

USER 1654:1654

EXPOSE 13301

ENTRYPOINT ["./ProxyNetworker.Server"]
