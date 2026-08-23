import type { CodegenConfig } from '@graphql-codegen/cli'

/**
 * Points at the real `schema.graphql` exported by RocketWiki.Api (design.md
 * §8 — code-first Hot Chocolate, SDL checked in at the repo root with a CI
 * drift guard). The frontend generates its whole typed client from that one
 * file; nothing here is hand-transcribed anymore.
 */
const config: CodegenConfig = {
  schema: '../schema.graphql',
  documents: ['src/graphql/operations/**/*.graphql'],
  generates: {
    'src/graphql/generated/graphql.ts': {
      // `typescript-operations` is self-contained — it generates its own
      // copies of every schema type an operation touches, so it doesn't
      // need the base `typescript` plugin alongside it. Running both in
      // the same output file causes duplicate declarations for any input
      // object type used directly as an operation variable (reproduced
      // with just `typescript` + `typescript-operations`, no urql plugin
      // involved — a real quirk of this plugin combination, not our
      // schema or operations).
      plugins: ['typescript-operations', 'typescript-urql'],
      config: {
        withHooks: true,
        // Hot Chocolate's custom scalars, mapped to what actually crosses
        // the JSON wire. Without these, codegen types every UUID/DateTime
        // field as `any` and the type-safety the round-trip to the real
        // schema is supposed to buy silently evaporates.
        scalars: {
          UUID: 'string',
          DateTime: 'string',
          Long: 'number',
          UnsignedByte: 'number',
        },
      },
    },
  },
}

export default config
