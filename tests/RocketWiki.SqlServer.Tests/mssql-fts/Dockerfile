# SQL Server with Full-Text Search, for tests/RocketWiki.SqlServer.Tests
# (design.md §14 tier 3).
#
# The official mcr.microsoft.com/mssql/server image ships WITHOUT Full-Text
# Search, and the InitialCreate migration's CREATE FULLTEXT CATALOG/INDEX fails
# hard (error 7609) on an instance that lacks it - so the container tier cannot
# use the stock image at all. Microsoft's documented fix for Linux is installing
# the mssql-server-fts package (learn.microsoft.com: "Install SQL Server
# Full-Text Search on Linux", package name verified for SQL Server 2025 /
# ver17 on 2026-08-23).
#
# Proven by CI run #19: the 2025 base image does NOT carry the
# packages.microsoft.com apt repo ("E: Unable to locate package
# mssql-server-fts"), so this file adds it first — key into
# /etc/apt/trusted.gpg.d (the pattern Microsoft's own mssql-docker full-text
# sample uses) and the mssql-server-2025 list matching the base's Ubuntu
# release, read from /etc/os-release rather than hardcoded (both 22.04 and
# 24.04 list URLs verified to exist on 2026-08-23).
#
# Base is 2025-latest, not 2022-latest, because design.md names SQL Server 2025
# as the production engine (§2; §9.3's native vector + DiskANN follow-up needs
# it), and the whole point of this tier is testing what production runs.
#
# Built two ways from this same file:
#  - CI: an explicit `docker build` step (cache-friendly, fails loudly if the
#    apt repo breaks), then handed to tests via ROCKETWIKI_MSSQL_FTS_IMAGE.
#  - Local dev with Docker: SqlServerContainerFixture builds it on first run via
#    Testcontainers' ImageFromDockerfile (cached by the daemon afterwards).
# Pinned by digest for reproducibility: CI run #28 crashed at engine boot on
# the SAME digest that ran green in #21-27 (a runner-side flake, proven by
# comparing build-log digests), and a digest pin removes tag drift as a
# variable in any future diagnosis. Bump deliberately, not by tag movement.
FROM mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1

# The base image runs as the non-root mssql user (SQL Server 2019+); apt needs root.
USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
         -o /etc/apt/trusted.gpg.d/microsoft.asc \
    && . /etc/os-release \
    && curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/mssql-server-2025.list" \
         -o /etc/apt/sources.list.d/mssql-server-2025.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

USER mssql
