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

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra

WORKDIR /app
COPY --from=build /app/publish/ ./

USER 1654:1654

EXPOSE 13301

ENTRYPOINT ["./ProxyNetworker.Server"]
