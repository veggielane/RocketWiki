# Organising a space

## The page tree

Pages nest. **Add child page** on any page creates one beneath it; **Move**
re-parents an existing page and its whole subtree.

Moving a page does not change its address, so links keep working. This is the
main reason the hierarchy is kept out of the URL.

## Labels

Labels are free-form tags, scoped to a space. Add them from the page itself.
The space browser can then filter the tree down to one label, and the
`page-list` widget can pull a list of labelled pages into another page.

## Page icons

A page can carry one of a fixed set of icons, chosen when you create or edit
it. It shows in the tree and beside the page title. Purely decoration — it
grants nothing and hides nothing.

## Properties

Properties are structured metadata beside a page rather than inside it —
`Owner`, `Review date`, `Status`. They read on the page and are edited on its
**Details** screen.

The available keys are defined by an instance admin, so a property means the
same thing everywhere rather than being spelled three ways.

## Forms and entries

A page can carry **entries**: structured records, not prose. Declare a form in
a fenced block:

```form-definition
collection = incident-report
field = severity: select(low, medium, high), required
field = summary: text, required
field = occurredAt: date
```

That block renders as a form to fill in. A second block lists what has been
submitted:

```form-list
collection = incident-report
columns = occurredAt, severity, summary
```

A page can hold as many collections as you like. Two things worth knowing:

- a collection belongs to **its page** — the same name on another page is a
  separate set of records, not a shared one;
- each record carries **its own marking**, so a single page can hold records at
  different classifications, and each reader sees only the ones they may read.

## Trash

Deleting a page moves it and its children to the space's trash, where it can be
restored. Restoring is refused if another page has taken its address in the
meantime — you are told which, so you can rename that one first.
