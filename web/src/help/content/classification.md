# Classifications and markings

Every page carries a **protective marking**. It appears as a banner at the top
and bottom of the page, so it travels with the text if the page is copied,
printed or screenshotted, and it decides who can read the page at all.

## The four parts of a marking

A marking reads left to right, for example:

```
UK SECRET APPLE NORTH AUS/NZ EYES ONLY
```

| Part | In the example | What it is |
|---|---|---|
| Prefix | `UK` | A national qualifier. On or off, and purely presentational |
| Classification | `SECRET` | One of four levels: OFFICIAL, OFFICIAL-SENSITIVE, SECRET, TOP SECRET, in that order |
| Selectors | `APPLE NORTH` | Extra codewords that narrow the audience within the classification. Each belongs to a category, and a page carries at most one value per category |
| Caveat | `AUS/NZ EYES ONLY` | The countries the page is releasable to, drawn from AUS, CAN, NZ, UK and US |

A page may have no selectors and no caveat, in which case the marking is just
the prefix and the level: `UK OFFICIAL`.

## What you need to read a page

Every one of these must be true. None of them can stand in for another.

- **Access to the space.** Someone who manages the space has granted access to
  you, your group, or everyone. Without this nothing else matters.
- **Clearance at or above the level.** Your clearance comes from your account,
  not from the wiki. If your account states none, you are treated as
  OFFICIAL-SENSITIVE: you can read the two everyday levels and nothing above.
- **Eligibility for each selector's category.** Some categories require an
  attribute on your account saying you may see that kind of material at all.
  Others are open to everyone.
- **A grant of each selector value in this space.** Being eligible for a
  category is not the same as being let in: an access grant in the space must
  carry the value the page uses.
- **A nationality in the caveat.** If the page names countries, your account
  must state at least one of them.
- **Every restriction on the page and its ancestors.** Restrictions narrow
  further, and they accumulate down the tree.

**The UK prefix changes nothing about access.** Switching it on or off alters
how the marking reads and nothing else. Nobody is granted or denied anything by
it.

## Why you sometimes see less than a colleague

A marking only ever **subtracts**. Being a space admin, or even an instance
admin, does not read around one: administration and clearance are separate
things, and so is access to the space itself. Two people looking at the same
space can legitimately see different pages, different search results and
different totals. That is the mechanism working.

## Why a page shows as (protected)

If you have access to a space but cannot read one of its pages, the page is
not hidden from you. In the page tree, on the page itself and behind a link,
it appears as **(protected)** with its marking and the reasons you cannot read
it. It has no title, no content and nothing beneath it.

Each reason is one sentence, and each has a remedy:

- **Needs SECRET clearance; you hold OFFICIAL-SENSITIVE.** Your clearance is
  below the page's level. Clearance is set on your account by whoever manages
  it, not by a space admin.
- **Not eligible for FRUIT material.** Your account does not carry the
  attribute that category requires. That, too, is an account matter.
- **APPLE is not granted to you in this space.** You are eligible, but no
  access grant in this space that matches you carries that value. Ask a space
  admin to add it to a grant.
- **Releasable to AUS/NZ only.** The page names countries and your account
  states none of them.
- **Blocked by a restriction rule.** A restriction on the page or one of its
  ancestors excludes you. A space admin can see which one.
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
- **You cannot mark a page out of your own reach.** A level above your
  clearance, a selector you are not eligible for or not granted here, or a
  caveat that excludes your nationality is refused, because otherwise you could
  write a page and immediately lose access to it. The picker greys those
  choices out before you get that far.
- A category can carry only one value per page.

Lowering a marking widens who can read the page, and that is recorded in the
audit log distinctly from raising one. Dropping the level, clearing or widening
the caveat, and removing or swapping a selector all count as lowering.
