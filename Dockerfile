FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets .editorconfig ./
COPY src/ src/
COPY assets/icons/eden-server.ico assets/icons/
COPY tools/Build-SkillClient.py tools/mod_client_loader.py tools/skill_client_overlay.py tools/
ARG EDEN_REVISION
RUN dotnet publish src/Eden.Server/Eden.Server.csproj -c Release -o /publish \
    -p:UseAppHost=false -p:SourceRevisionId="$EDEN_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /publish/ ./
RUN install -d -o app -g app /data
USER $APP_UID
ENV EDEN_DATA_DIR=/data
EXPOSE 5222 26000 26001 26002 26003
ENTRYPOINT ["dotnet", "Eden.Server.dll"]
CMD ["--config", "/config/server.json"]
