FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

# Official leanprover/lean4 v4.30.0 Linux x64 release asset; digest from the
# GitHub release API. Installation is build-time only; runtime has no network.
ARG LEAN_VERSION=4.30.0
ARG LEAN_LINUX_SHA256=4dad74141c2c119ca1aa626656be83b8e14238afba97271fd7bf1eb3f081b319

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    MPLCONFIGDIR=/tmp/matplotlib \
    PATH=/opt/lean/bin:${PATH}

COPY requirements.lock /tmp/requirements.lock

RUN set -eux; \
    test "$(dpkg --print-architecture)" = amd64; \
    for attempt in 1 2 3; do \
        rm -rf /var/lib/apt/lists/*; \
        apt-get clean; \
        if apt-get -o Acquire::Retries=5 -o Acquire::http::No-Cache=true -o Acquire::https::No-Cache=true update \
            && apt-get -o Acquire::Retries=5 -o Acquire::http::No-Cache=true -o Acquire::https::No-Cache=true install -y --no-install-recommends ca-certificates curl zstd libgmp10 libatomic1 libstdc++6; then \
            break; \
        fi; \
        if [ "$attempt" -eq 3 ]; then exit 1; fi; \
        sleep "$((attempt * 5))"; \
    done; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 \
        "https://github.com/leanprover/lean4/releases/download/v${LEAN_VERSION}/lean-${LEAN_VERSION}-linux.tar.zst" \
        --output /tmp/lean.tar.zst \
    && echo "${LEAN_LINUX_SHA256}  /tmp/lean.tar.zst" | sha256sum --check --strict -; \
    mkdir -p /opt/lean; \
    tar --zstd --extract --file /tmp/lean.tar.zst --directory /opt/lean --strip-components=1; \
    test "$(/opt/lean/bin/lean --short-version)" = "${LEAN_VERSION}"; \
    rm -f /tmp/lean.tar.zst; \
    rm -rf /var/lib/apt/lists/*; \
    apt-get clean; \
    groupadd --gid 10001 research; \
    useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin research; \
    python -m pip install --no-cache-dir --require-hashes --requirement /tmp/requirements.lock; \
    rm -f /tmp/requirements.lock; \
    mkdir -p /sandbox; \
    chown research:research /sandbox

USER 10001:10001
WORKDIR /sandbox/work
ENTRYPOINT []
