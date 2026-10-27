FROM debian:trixie-slim
ARG MIRROR=deb.debian.org
RUN sed -i "s|deb.debian.org|${MIRROR}|g" /etc/apt/sources.list.d/debian.sources \
 && apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    imagemagick libtiff-tools webp libopenjp2-tools jbig2 python3-pil fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
