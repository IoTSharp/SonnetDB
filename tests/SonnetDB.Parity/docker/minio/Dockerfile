# Keep the existing parity reference version available after its public image
# registry was retired. This builds unmodified upstream integration-test code.
FROM golang:1.23.12-bookworm AS build
WORKDIR /src
RUN curl --fail --location --retry 3 \
      https://codeload.github.com/minio/minio/tar.gz/03e996320ebb887112fb2a15c6f27936e5f124a0 \
      --output /tmp/minio.tar.gz \
    && echo '23783181b83d426a01dad69524b0fccc19796fd475f40cb9144237c6ce515c22  /tmp/minio.tar.gz' | sha256sum --check - \
    && tar -xzf /tmp/minio.tar.gz --strip-components=1 \
    && rm /tmp/minio.tar.gz
RUN CGO_ENABLED=0 go build -trimpath -o /out/minio .

FROM debian:bookworm-slim
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/minio /usr/local/bin/minio
COPY --from=build /src/LICENSE /usr/share/doc/minio/LICENSE
COPY --from=build /src /usr/share/minio/source
ENTRYPOINT ["/usr/local/bin/minio"]
