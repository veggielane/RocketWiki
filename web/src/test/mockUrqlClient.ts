import { Client, type Exchange, type Operation } from 'urql'
import { Kind, type OperationDefinitionNode } from 'graphql'
import { filter, map, pipe } from 'wonka'

export interface RecordedOperation {
  name: string
  kind: Operation['kind']
  variables: Record<string, unknown>
  /**
   * `context.additionalTypenames` as the operation actually carried it. Recorded
   * because a query that depends on types it does not select (the page tree, on
   * every page mutation) is only correct if this reaches the exchange — and a
   * missing declaration is invisible in the rendered output, which is how the
   * sidebar shipped never refreshing after a page was created.
   */
  additionalTypenames: readonly string[]
}

export interface MockClient {
  client: Client
  /** Every non-teardown operation the client executed, in order — assert on mutation variables etc. */
  operations: RecordedOperation[]
}

function operationName(op: Operation): string {
  const def = op.query.definitions.find(
    (d): d is OperationDefinitionNode => d.kind === Kind.OPERATION_DEFINITION,
  )
  return def?.name?.value ?? '(anonymous)'
}

/**
 * A urql client whose single exchange answers every operation from
 * `respond`, keyed by operation name — the test idiom for components built
 * on the *generated* hooks: the component runs its real query/mutation
 * documents, and the test controls only the wire responses. No network, no
 * cache; every execution hits `respond` and is recorded.
 */
export function createMockUrqlClient(respond: (name: string, op: Operation) => Record<string, unknown> | undefined): MockClient {
  const operations: RecordedOperation[] = []

  const mockExchange: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      map((op: Operation) => {
        const name = operationName(op)
        operations.push({
          name,
          kind: op.kind,
          variables: (op.variables ?? {}) as Record<string, unknown>,
          additionalTypenames: op.context.additionalTypenames ?? [],
        })
        return {
          operation: op,
          data: respond(name, op),
          stale: false,
          hasNext: false,
        }
      }),
    )

  return { client: new Client({ url: '/graphql', exchanges: [mockExchange] }), operations }
}
