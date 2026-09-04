# Classifications and markings

Every page carries a **protective marking**. It appears as a banner at the top
and bottom of the page, so it travels with the text if the page is copied,
printed or screenshotted. Two of its four parts decide who can read the page at
all; the other two say what the page is.

## The four parts of a marking

A marking reads left to right, for example:

```
UK SECRET APPLE NORTH AUS/NZ EYES ONLY
```

| Part | In the example | What it is |
|---|---|---|
| Prefix | `UK` | A national qualifier. On or off, and purely presentational |
| Classification | `SECRET` | One of four levels: OFFICIAL, OFFICIAL-SENSITIVE, SECRET, TOP SECRET, in that order. It states how sensitive the page is, and decides nothing about who may read it |
| Selectors | `APPLE NORTH` | Codewords that narrow the audience. Each belongs to a category, a page carries at most one value per category, and a reader must hold every value the page carries |
| Caveat | `AUS/NZ EYES ONLY` | The countries the page is releasable to, drawn from AUS, CAN, NZ, UK and US |

A page may have no selectors and no caveat, in which case the marking is just
the prefix and the level: `UK OFFICIAL`.

**The classification and the UK prefix say what the page is. The selectors and
the caveat decide who may read it.** There is no clearance in this deployment
for a level to be measured against, so a page marked TOP SECRET is readable by
exactly the same people as it would be marked OFFICIAL: those the space, its
selectors, its caveat and its restrictions let in. The level is there so that a
reader, a printout and a search result all say how sensitive the content is.

## What you need to read a page

Every one of these must be true. None of them can stand in for another.

- **Access to the space.** Someone who manages the space has granted access to
  you, your group, or everyone. Without this nothing else matters.
- **A grant of each selector value in this space.** For every selector the
  page carries, an access grant in the space that matches you must carry that
  value. Grants are the only source of selector values: nothing on your account
  makes you eligible for a category, and nothing on it shuts you out of one.
- **A nationality in the caveat.** If the page names countries, your account
  must state at least one of them.
- **Every restriction on the page and its ancestors.** Restrictions narrow
  further, and they accumulate down the tree.

**The classification changes nothing about access, and neither does the UK
prefix.** Changing either alters how the marking reads and nothing else. Nobody
is granted or denied anything by them.

## Why you sometimes see less than a colleague

A marking only ever **subtracts**. Being a space admin, or even an instance
admin, does not read around one: administration and access are separate
things, and so is access to the space itself. Two people looking at the same
space can legitimately see different pages, different search results and
different totals, because they hold different grants and different
nationalities. That is the mechanism working.

## Why a page shows as (protected)

If you have access to a space but cannot read one of its pages, the page is
not hidden from you. In the page tree, on the page itself and behind a link,
it appears as **(protected)** with its marking and the reasons you cannot read
it. It has no title, no content and nothing beneath it.

Each reason is one sentence, and each has a remedy:

- **APPLE is not granted to you in this space.** No access grant in this space
  that matches you carries that value. Ask a space admin to add it to a grant.
- **Releasable to AUS/NZ only.** The page names countries and your account
  states none of them. Nationality is set on your account by whoever manages
  it, not by a space admin.
- **Blocked by a restriction rule.** A restriction on the page or one of its
  ancestors excludes you. A space admin can see which one.
- **This page's marking is missing, so nobody can read it until it is restored.** The page has lost its marking record. This one is not about you: no reader can open the page, whatever they hold, and an editor or admin needs to set its marking again.
- **You have no access to this space.** No access grant in the space matches
  you. Ask a space admin for one.

**If you have no access to the space, that last sentence is all you see.** The
marking is withheld and no other reason is listed, because what is marked how
inside a space you cannot enter is not yours to know. The space's tree is
empty to you and the space does not appear in your lists.

Search, Ask the wiki, page lists, feeds and counts still **omit** a page you
cannot read rather than showing a placeholder. A result list is a compilation
of what matched, and a placeholder in it would be a count of what you may not
see.

## Setting a marking

Editors set a page's marking on its **Details** screen: the level, the UK
prefix switch, one picker per selector category, and the caveat countries.

- A new page inherits its parent's whole marking, so a child is never
  accidentally less protected than the page it sits under.
- **Any level may be set.** The classification is compared against nobody, so
  every level is offered to every editor. Pick the one that says what the page
  is.
- **You cannot mark a page out of your own reach.** A selector value you are
  not granted in this space, or a caveat that excludes your own nationality, is
  refused, because otherwise you could write a page and immediately lose access
  to it. The picker greys those choices out before you get that far, and says
  why beside each one.
- A category can carry only one value per page.

Lowering a marking widens who can read the page, or lowers the statement of
what it is, and either is recorded in the audit log distinctly from raising
one. Dropping the level, clearing or widening the caveat, and removing or
swapping a selector all count as lowering. The level counts even though it
gates nothing here, because a declassification is still the fact a reviewer
looks for.
