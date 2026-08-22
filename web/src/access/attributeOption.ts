/** The subset of `AttributeDefinition` (design.md §6.2) the rule builder's attr-condition picker needs. */
export interface AttributeOption {
  key: string
  displayName?: string
  allowedValues: string[]
}
