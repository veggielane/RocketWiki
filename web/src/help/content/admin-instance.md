# Administering the instance

Under **Admin**, for instance admins.

## Analytics

The same report as a space's, across every space you can see. Activity over
time, most viewed and edited pages, contributors and readers, content health,
and the searches people ran — including the ones that found nothing, which is
usually the clearest signal of a gap in the wiki.

No panel anywhere breaks anything down by classification. A chart bucketed that
way would be a census of how much classified material exists, so the number is
not produced rather than being produced and hidden.

## Audit log

Every action that touched content or permissions: who, what, when, from where,
and through which channel. Filterable and exportable.

Denials are recorded as well as successes — a refused read is often the more
interesting row.

## Property keys

The vocabulary of page property keys. Defining them centrally is what stops
`Owner`, `owner` and `Page owner` becoming three different fields.

A key in use cannot be deleted; you are told how many pages hold it.

## Custom emojis

Instance-wide `:name:` emojis. Content referencing an unknown name degrades to
the literal text rather than breaking.

## Sync status

For instances that exchange content across a boundary. Shows what has been
exported and what has been imported, per space and per origin, with the
sequence positions — a gap or a stalled position is the thing to look for.

Content crosses; **permissions do not**. Grants, roles and a space's default
page are decided locally by whichever instance holds the replica. A
classification does cross, and an arriving page with no declared marking is
treated as TOP SECRET rather than guessed at.

## What an admin cannot do

Instance admin is not a clearance. It grants no access to content above your
own clearance, and there is no override — pages you may not read are absent
from your search results, your trees, and your analytics totals, exactly as
they are for anyone else.
