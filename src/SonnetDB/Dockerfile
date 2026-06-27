# SonnetDB multi-stage Docker build.
# Build from the repository root:
#   docker build -f src/SonnetDB/Dockerfile -t iotsharp/sonnetdb .
#
# Run:
#   docker run -p 5080:5080 -v sonnetdb-data:/data iotsharp/sonnetdb

FROM node:24-bookworm-slim AS node-tooling

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY --from=node-tooling /usr/local/bin/ /usr/local/bin/
COPY --from=node-tooling /usr/local/lib/node_modules/ /usr/local/lib/node_modules/

# Keep npm/npx as symlinks to the npm package entrypoints.
# Copying single files from another stage can dereference symlinks and break relative requires.
RUN ln -sf ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -sf ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# Fail fast if Node/NPM wiring is broken in this stage.
RUN node -v && npm -v

# Copy build config and .csproj files first to maximize Docker layer cache for restore.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY .config/dotnet-tools.json .config/
COPY src/SonnetDB/SonnetDB.csproj src/SonnetDB/
COPY src/SonnetDB.Core/SonnetDB.Core.csproj src/SonnetDB.Core/

RUN dotnet restore src/SonnetDB/SonnetDB.csproj
RUN dotnet tool restore

# Copy full source and assets needed for the actual build.
COPY src/SonnetDB/ src/SonnetDB/
COPY src/SonnetDB.Core/ src/SonnetDB.Core/
COPY docs/ docs/
COPY web/ web/

RUN rm -rf src/SonnetDB/wwwroot/help \
    && dotnet tool run jekyllnet build --source docs --destination src/SonnetDB/wwwroot/help

RUN dotnet publish src/SonnetDB/SonnetDB.csproj \
    -c Release \
    -p:BuildAdminUi=true \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=build /repo/src/SonnetDB/appsettings.Container.json ./appsettings.json

# 把源码里的知识库素材随镜像一起发布，让 CopilotDocsIngestionService /
# CopilotSkillsIngestionService 在容器首次启动时即可把 docs / 技能向量化进
# `__copilot__.docs` / `__copilot__.skills`，无需挂载额外卷。
# 默认 Roots 是 ["./docs", "./web/help", "./src/SonnetDB/wwwroot/help"]，
# 默认 Skills.Root 是 "./copilot/skills"，工作目录为 /app，所以拷贝到这两个相对路径下即可。
COPY docs/ /app/docs/
COPY copilot/ /app/copilot/

RUN mkdir -p /data

EXPOSE 5080

ENTRYPOINT ["dotnet", "SonnetDB.dll"]
