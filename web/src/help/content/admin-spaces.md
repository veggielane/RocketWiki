# Administering a space

Reached from **Space settings** — offered only to people who can manage the
space, which means an instance admin or that space's own space-admin.

## Details

Name and description are editable. The **key** is not: it is in every URL and
every sync bundle's identity, so changing it would be a migration rather than a
setting.

## Default page

A space can name a page as its default. `/spaces/{key}` then lands there
instead of on the browser, which is the natural home for an index or a
welcome page.

If the default page is one the reader cannot see, the space behaves as though it
had none rather than becoming a dead end — the same "absent, not forbidden"
rule as everywhere else. The browser is always reachable at
`/spaces/{key}/-/browse`.

## Grants

A grant gives a **role** to everyone matching a **rule** — a group, an
attribute, or everyone. Roles:

| Role | Can |
|---|---|
| Viewer | read |
| Commenter | read, comment |
| Editor | read, comment, create, edit, move, delete |
| Space admin | all of the above, plus manage the space and its grants |

Grants add; page restrictions subtract. A classification subtracts from
everyone, admins included.

## Restrictions

A page can carry restrictions of its own, which apply to it and everything
beneath it. They can only narrow what a grant allows, never widen it.

## Trash

Deleted pages, grouped by the delete that produced them, restorable as a batch.

## Analytics

What is being read and edited in this space, over the pages **you** can see.

Two admins with different clearances therefore see different numbers, and the
header states the page count a report covered so nobody mistakes it for the
whole space. Reading the report is itself recorded in the audit log, because it
names who read what.
