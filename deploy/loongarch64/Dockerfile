ARG LOONG_BASE_IMAGE=ghcr.io/loong64/debian@sha256:a1343939248f40fd78f2bc7e3adebdb8623a7984f4708a4f43fc4f16eb3c4d0c
FROM --platform=linux/loong64 ${LOONG_BASE_IMAGE}

RUN apt-get update -o Acquire::Retries=1 -o Acquire::http::Timeout=20 \
    && apt-get install -y -o Acquire::Retries=1 -o Acquire::http::Timeout=20 --no-install-recommends \
        ca-certificates curl libgssapi-krb5-2 libicu76 \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_ROOT=/opt/dotnet
ENV PATH=/opt/dotnet:${PATH}
WORKDIR /app

# The dotnet and published named contexts come from a verified native LoongArch build.
COPY --from=dotnet /dotnet /opt/dotnet/dotnet
COPY --from=dotnet /host/ /opt/dotnet/host/
COPY --from=dotnet /shared/ /opt/dotnet/shared/
COPY --from=published / /app/
COPY src/SonnetDB/appsettings.Container.json /app/appsettings.json
COPY docs/ /app/docs/
COPY copilot/ /app/copilot/

RUN mkdir -p /data

EXPOSE 5080 5081 1883
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:5080/healthz/live || exit 1

ENTRYPOINT ["/opt/dotnet/dotnet", "/app/SonnetDB.dll"]
