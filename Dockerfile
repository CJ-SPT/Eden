# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets .editorconfig ./
# Restore only when project dependencies change, and retain downloaded packages between builds.
COPY src/Eden.Data/Eden.Data.csproj src/Eden.Data/
COPY src/Eden.Gameplay/Eden.Gameplay.csproj src/Eden.Gameplay/
COPY src/Eden.Infrastructure/Eden.Infrastructure.csproj src/Eden.Infrastructure/
COPY src/Eden.Modding/Eden.Modding.csproj src/Eden.Modding/
COPY src/Eden.Protocol/Eden.Protocol.csproj src/Eden.Protocol/
COPY src/Eden.Server/Eden.Server.csproj src/Eden.Server/
COPY src/Eden.World/Eden.World.csproj src/Eden.World/
RUN --mount=type=cache,id=eden-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Eden.Server/Eden.Server.csproj -p:Configuration=Release -p:UseAppHost=false
COPY src/ src/
COPY assets/icons/eden-server.ico assets/icons/
COPY tools/Build-SkillClient.py tools/mod_client_loader.py tools/skill_client_overlay.py tools/
ARG EDEN_REVISION
RUN --mount=type=cache,id=eden-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Eden.Server/Eden.Server.csproj -c Release --no-restore -o /publish \
    -p:UseAppHost=false -p:SourceRevisionId="$EDEN_REVISION" \
    && test -s /publish/wwwroot/_framework/blazor.web.js

FROM ubuntu:24.04 AS mod-compiler
WORKDIR /build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential ca-certificates curl xz-utils \
    && curl --fail --silent --show-error --location https://www.python.org/ftp/python/2.7.18/Python-2.7.18.tar.xz -o python.tar.xz \
    && echo 'b62c0e7937551d0cc02b8fd5cb0f544f9405bafc9a54d3808ed4594812edef43  python.tar.xz' | sha256sum --check \
    && tar -xf python.tar.xz --strip-components=1 \
    && ./configure --prefix=/opt/eden-python2 --without-ensurepip --enable-unicode=ucs2 \
    && make -j "$(nproc)" python \
    && install -D -m 0755 python /opt/eden-python2/bin/python2.7 \
    && mkdir -p /opt/eden-python2/lib/python2.7/lib-dynload \
    && cp -r Lib/. /opt/eden-python2/lib/python2.7/ \
    && /opt/eden-python2/bin/python2.7 -E -s -S -c 'import marshal; assert marshal.loads(marshal.dumps(42)) == 42'

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
# Keep OS packages independent of changing server binaries.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app /data
COPY --from=mod-compiler /opt/eden-python2/ /opt/eden-python2/
COPY --from=build /publish/ ./
USER $APP_UID
ENV EDEN_DATA_DIR=/data \
    EDEN_PYTHON=/usr/bin/python3 \
    EDEN_PYTHON2=/opt/eden-python2/bin/python2.7
EXPOSE 5222 26000 26001 26002 26003
ENTRYPOINT ["dotnet", "Eden.Server.dll"]
CMD ["--config", "/config/server.json"]
