import { describe, expect, it } from 'vitest'
import { allOf, anyOf, attr, everyone, group, user } from '../ruleTypes'
import { MalformedRuleExpressionError, parseRuleNode, serializeRuleNode, tryParseRuleNode } from '../ruleSerializer'

/**
 * The critical contract test file. `src/RocketWiki.Core/Access/RuleExpressionSerializer.cs`
 * (and its test suite, `RuleExpressionSerializerTests.cs`) is the canonical
 * definition of this JSON shape — every case here is chosen to mirror a case
 * there, so a drift between the two implementations shows up as a failing
 * test on whichever side goes stale.
 */
describe('serializeRuleNode — exact JSON output', () => {
  it('everyone', () => {
    expect(serializeRuleNode(everyone())).toBe('{"everyone":true}')
  })

  it('group', () => {
    expect(serializeRuleNode(group('engineering'))).toBe('{"group":"engineering"}')
  })

  it('user', () => {
    expect(serializeRuleNode(user('sub-123'))).toBe('{"user":"sub-123"}')
  })

  it('attr with an "in" list', () => {
    expect(serializeRuleNode(attr('nationality', ['NZ', 'US']))).toBe('{"attr":"nationality","in":["NZ","US"]}')
  })

  it('the §6.3 worked example: engineering AND (nationality in NZ/US OR export-cleared)', () => {
    const node = allOf([
      group('engineering'),
      anyOf([attr('nationality', ['NZ', 'US']), group('export-cleared')]),
    ])

    expect(serializeRuleNode(node)).toBe(
      '{"allOf":[{"group":"engineering"},{"anyOf":[{"attr":"nationality","in":["NZ","US"]},{"group":"export-cleared"}]}]}',
    )
  })
})

describe('parseRuleNode', () => {
  it('parses everyone', () => {
    expect(parseRuleNode('{ "everyone": true }')).toEqual(everyone())
  })

  it('parses group', () => {
    expect(parseRuleNode('{ "group": "engineering" }')).toEqual(group('engineering'))
  })

  it('parses user', () => {
    expect(parseRuleNode('{ "user": "sub-123" }')).toEqual(user('sub-123'))
  })

  it('parses attr with an "in" list', () => {
    expect(parseRuleNode('{ "attr": "nationality", "in": ["NZ", "US"] }')).toEqual(attr('nationality', ['NZ', 'US']))
  })

  it('parses the nested allOf/anyOf example from design.md §6.3', () => {
    const json = `{
      "allOf": [
        { "group": "engineering" },
        { "anyOf": [
          { "attr": "nationality", "in": ["NZ", "US"] },
          { "group": "export-cleared" }
        ]}
      ]
    }`

    const node = parseRuleNode(json)
    expect(node).toEqual(
      allOf([group('engineering'), anyOf([attr('nationality', ['NZ', 'US']), group('export-cleared')])]),
    )
  })
})

describe('serialize -> parse round trip', () => {
  it('round-trips the worked example', () => {
    const original = allOf([
      group('engineering'),
      anyOf([attr('nationality', ['NZ', 'US']), group('export-cleared')]),
    ])

    const json = serializeRuleNode(original)
    const roundTripped = parseRuleNode(json)
    expect(serializeRuleNode(roundTripped)).toBe(json)
    expect(roundTripped).toEqual(original)
  })
})

describe('parseRuleNode — malformed input is rejected exactly like the backend', () => {
  const malformedCases: [name: string, json: string][] = [
    ['not valid json', 'not valid json {{{'],
    ['empty string', ''],
    ['whitespace only', '   '],
    ['null', 'null'],
    ['bare number', '42'],
    ['bare string', '"just a string"'],
    ['empty array', '[]'],
    ['a "not" combinator (no NOT rules — design.md §6.3)', '{ "not": { "group": "engineering" } }'],
    ['a "deny" combinator (no deny rules — design.md §6.3)', '{ "deny": { "group": "engineering" } }'],
    ['everyone: false', '{ "everyone": false }'],
    ['everyone: "true" (string, not boolean)', '{ "everyone": "true" }'],
    ['group: 123 (not a string)', '{ "group": 123 }'],
    ['group: "" (empty)', '{ "group": "" }'],
    ['group: "   " (whitespace only)', '{ "group": "   " }'],
    ['user: "" (empty)', '{ "user": "" }'],
    ['attr without "in"', '{ "attr": "nationality" }'],
    ['"in" without "attr"', '{ "in": ["NZ"] }'],
    ['attr with empty "in" array', '{ "attr": "nationality", "in": [] }'],
    ['attr with "in" as a scalar, not an array', '{ "attr": "nationality", "in": "NZ" }'],
    ['attr with non-string "in" entries', '{ "attr": "nationality", "in": [1, 2] }'],
    ['attr: 1 (not a string)', '{ "attr": 1, "in": ["NZ"] }'],
    ['group + user combined (not an exact recognized key set)', '{ "group": "engineering", "user": "sub-1" }'],
    ['allOf + group combined', '{ "allOf": [{ "everyone": true }], "group": "engineering" }'],
    ['empty allOf', '{ "allOf": [] }'],
    ['empty anyOf', '{ "anyOf": [] }'],
    ['allOf as a non-array', '{ "allOf": "not-an-array" }'],
    ['a malformed nested node poisons the whole tree (allOf)', '{ "allOf": [ { "group": "engineering" }, { "bogus": true } ] }'],
    ['a malformed nested node poisons the whole tree (anyOf)', '{ "anyOf": [ { "everyone": true }, { "bogus": true } ] }'],
    ['a rule node must be an object, not a bare string in an array', '{ "allOf": ["engineering"] }'],
  ]

  it.each(malformedCases)('%s', (_name, json) => {
    expect(() => parseRuleNode(json)).toThrow(MalformedRuleExpressionError)
  })
})

describe('tryParseRuleNode', () => {
  it('returns node + null error for valid input', () => {
    const result = tryParseRuleNode('{ "everyone": true }')
    expect(result.error).toBeNull()
    expect(result.node).toEqual(everyone())
  })

  it('returns null node + an error message for malformed input', () => {
    const result = tryParseRuleNode('{ "bogus": true }')
    expect(result.node).toBeNull()
    expect(result.error).toBeTruthy()
  })
})
