import type { CodegenConfig } from '@graphql-codegen/cli'

/**
 * TEMPORARY: points at `src/graphql/schema.placeholder.graphql`, a
 * hand-transcribed guess at the real schema, because RocketWiki.Api hasn't
 * exported `schema.graphql` yet (design.md §8 — the backend is a code-first
 * Hot Chocolate schema, checked in at the repo root once it exists).
 *
 * Swap the `schema` path below to `../schema.graphql` the moment that file
 * exists and re-run `npm run codegen`. Nothing else in this config should
 * need to change.
 */
const config: CodegenConfig = {
  schema: './src/graphql/schema.placeholder.graphql',
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
      },
    },
  },
}

export default config
