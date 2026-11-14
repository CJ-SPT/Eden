FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets .editorconfig ./
COPY src/ src/
COPY assets/icons/eden-server.ico assets/icons/
COPY tools/Build-SkillClient.py tools/mod_client_loader.py tools/skill_client_overlay.py tools/
ARG EDEN_REVISION
RUN dotnet publish src/Eden.Server/Eden.Server.csproj -c Release -o /publish \
    -p:UseAppHost=false -p:SourceRevisionId="$EDEN_REVISION"

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
COPY --from=build /publish/ ./
COPY --from=mod-compiler /opt/eden-python2/ /opt/eden-python2/
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app /data
USER $APP_UID
ENV EDEN_DATA_DIR=/data \
    EDEN_PYTHON=/usr/bin/python3 \
    EDEN_PYTHON2=/opt/eden-python2/bin/python2.7
EXPOSE 5222 26000 26001 26002 26003
ENTRYPOINT ["dotnet", "Eden.Server.dll"]
CMD ["--config", "/config/server.json"]
