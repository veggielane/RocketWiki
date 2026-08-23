# SQL Server with Full-Text Search, for tests/RocketWiki.SqlServer.Tests
# (design.md §14 tier 3).
#
# The official mcr.microsoft.com/mssql/server image ships WITHOUT Full-Text
# Search, and the InitialCreate migration's CREATE FULLTEXT CATALOG/INDEX fails
# hard (error 7609) on an instance that lacks it - so the container tier cannot
# use the stock image at all. Microsoft's documented fix for Linux is installing
# the mssql-server-fts package from the repo the base image already carries
# (learn.microsoft.com: "Install SQL Server Full-Text Search on Linux",
# package name verified for SQL Server 2025 / ver17 on 2026-08-23).
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
FROM mcr.microsoft.com/mssql/server:2025-latest

# The base image runs as the non-root mssql user (SQL Server 2019+); apt needs root.
USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

USER mssql
