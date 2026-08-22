# Deployment Guide

This page covers the **production** rollout. See the [Runbook](page://11111111-1111-1111-1111-111111111111) for incident response.

## Steps

1. Build the release artifact.
2. Run the smoke tests.

:::warning
Do not deploy on a Friday.
:::

## Rollback matrix

| Environment | Command |
| --- | --- |
| staging | `deploy rollback staging` |
| production | `deploy rollback prod` |
