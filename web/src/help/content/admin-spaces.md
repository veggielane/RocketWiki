# Administering a space

Reached from **Space settings** — offered only to people who can manage the
space, which means an instance admin or that space's own space admin.

## Details

Name and description are editable. The **key** is not: it is in every URL and
every sync bundle's identity, so changing it would be a migration rather than a
setting.

## Default page

A space can name a page as its default. `/spaces/{key}` then lands there
instead of on the browser, which is the natural home for an index or a
welcome page.

If the default page is one the reader cannot see, the space behaves as though it
had none rather than becoming a dead end. The browser is always reachable at
`/spaces/{key}/-/browse`.

## Access grants: who may see the space

An access grant lets everyone matching a **rule** — a group, an attribute, a
user, or everyone — see the space's pages. It can also carry **selector
values**, which let those readers open pages marked with that codeword.

| An access grant has | Meaning |
|---|---|
| A rule | Who this grant applies to |
| Selector values (optional) | Which selector values those people hold in this space, for example `APPLE` in the `FRUIT` category |

Grants add. Someone who matches two access grants holds the selector values of
both. Nobody sees anything in a space until an access grant matches them, and
an access grant on its own lets people read, not edit.

Holding a selector value here is only half of what a reader needs for a page
marked with it: their account must also make them eligible for that category.
A grant to someone who is not eligible confers nothing, and eligibility without
a grant opens nothing.

## Role grants: who may edit or administer

A role grant gives everyone matching a rule a **role**. It confers no
visibility of its own.

| Role | Can |
|---|---|
| Editor | Create, edit, move and delete pages they can read; add labels |
| Space admin | All of the above, plus manage the space, its grants and its restrictions |

**Roles never let anyone see more.** A space admin who matches no access grant
can open these settings and edit every grant, and sees every page in the space
as **(protected)**, unable to read any of them. An instance admin is in exactly
the same position. The way in is an access grant, which the admin can write for
themselves — and which is recorded in the audit log when they do.

## Creating a space

A new space needs at least one **space admin** role grant, so that somebody can
always administer it; the form pre-fills that with you. Access is optional at
creation. **A new space is visible to nobody until an access grant is added**,
which is deliberate: opening a space to everyone should be a decision, never a
default.

## Restrictions

A page can carry restrictions of its own, which apply to it and everything
beneath it. They can only narrow what a grant allows, never widen it. A
classification, and the rest of a marking, subtracts from everyone, admins
included.

## Trash

Deleted pages, grouped by the delete that produced them, restorable as a batch.

## Analytics

What is being read and edited in this space, over the pages **you** can see.

Two admins with different clearances therefore see different numbers, and the
header states the page count a report covered so nobody mistakes it for the
whole space. Reading the report is itself recorded in the audit log, because it
names who read what.
