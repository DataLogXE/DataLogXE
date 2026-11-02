#!/usr/bin/env bash
# DataLogXE - A simple and high performance data logger.
# Copyright (C) 2026 datalogxe@gmail.com
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program.  If not, see <http://www.gnu.org/licenses/>.

# Build datalogxe/datalogxe for linux/amd64 and linux/arm64, then push the manifests.
# The proxy runs the official Caddy image of compose.yaml with the Caddyfile of the app; it is validated here first.
# Version defaults to <Version> in Directory.Build.props.
# Override: ./docker-build-push.sh 0.0.100
#           IMAGE=datalogxe/datalogxe VERSION=0.0.100 ./docker-build-push.sh
#           PLATFORMS=linux/arm64 ./docker-build-push.sh
# Apply buildkitd.toml (optional, next to this script) to an existing builder: RECREATE_BUILDER=1 ./docker-build-push.sh
# Registry build cache (optional): USE_REGISTRY_CACHE=1 CACHE_REF=datalogxe/datalogxe:buildcache ./docker-build-push.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "${ROOT}"

IMAGE="${IMAGE:-datalogxe/datalogxe}"
PLATFORMS="${PLATFORMS:-linux/amd64,linux/arm64}"
BUILDER="${BUILDER:-xe-multi}"
BUILDKITD_CONFIG="${BUILDKITD_CONFIG:-${ROOT}/buildkitd.toml}"
RECREATE_BUILDER="${RECREATE_BUILDER:-0}"
USE_REGISTRY_CACHE="${USE_REGISTRY_CACHE:-0}"
CACHE_REF="${CACHE_REF:-${IMAGE}:buildcache}"

if [[ -n "${1:-}" ]]; then
  VERSION="$1"
elif [[ -z "${VERSION:-}" ]]; then
  VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
fi

if [[ -z "${VERSION}" ]]; then
  echo "No version. Pass it as the first argument or set VERSION." >&2
  exit 1
fi

TAG="${IMAGE}:${VERSION}"

ensure_builder() {
  if [[ "${RECREATE_BUILDER}" == "1" ]]; then
    echo "==> recreate builder ${BUILDER}"
    docker buildx rm "${BUILDER}" 2>/dev/null || true
  fi

  local create_args=(
    --name "${BUILDER}"
    --driver docker-container
    --platform "${PLATFORMS}"
    --use
  )
  if [[ -f "${BUILDKITD_CONFIG}" ]]; then
    create_args+=(--buildkitd-config "${BUILDKITD_CONFIG}")
  elif [[ ! -f "${BUILDKITD_CONFIG}" ]]; then
    echo "No BuildKit config at ${BUILDKITD_CONFIG}; using BuildKit defaults." >&2
  fi

  if ! docker buildx inspect "${BUILDER}" >/dev/null 2>&1; then
    docker buildx create "${create_args[@]}"
  else
    docker buildx use "${BUILDER}"
  fi
  docker buildx inspect --bootstrap >/dev/null
}

CADDY_IMAGE="$(sed -n 's:^ *image\: *\(caddy\:[^ ]*\).*:\1:p' compose.yaml | head -1)"
echo "==> validate DataLogApi/Proxy/Caddyfile with ${CADDY_IMAGE}"
docker run --rm -v "${ROOT}/DataLogApi/Proxy/Caddyfile:/etc/caddy/Caddyfile:ro" "${CADDY_IMAGE}" \
  caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

echo "==> builder ${BUILDER} (${PLATFORMS})"
ensure_builder

build_args=(
  -f Dockerfile
  --platform "${PLATFORMS}"
  --build-arg "VERSION=${VERSION}"
  -t "${TAG}"
)

if [[ "${USE_REGISTRY_CACHE}" == "1" ]]; then
  echo "==> registry cache ${CACHE_REF}"
  build_args+=(
    --cache-from="type=registry,ref=${CACHE_REF}"
    --cache-to="type=registry,ref=${CACHE_REF},mode=max"
  )
fi

build_args+=(--push .)

echo "==> build and push ${TAG}"
docker buildx build "${build_args[@]}"

echo "Pushed ${TAG}"
