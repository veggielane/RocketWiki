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
interesting row. A denial names the first reason the read failed; the page
itself, where it shows as (protected), lists every reason.

## Property keys

The vocabulary of page property keys. Defining them centrally is what stops
`Owner`, `owner` and `Page owner` becoming three different fields.

A key in use cannot be deleted; you are told how many pages hold it.

## Custom emojis

Instance-wide `:name:` emojis. Content referencing an unknown name degrades to
the literal text rather than breaking.

## Selector categories

The selector categories a marking can use — a name such as `FRUIT`, its values
such as `APPLE` and `BANANA`, and whether readers need an account attribute to
be eligible for it — are **configured by the operators in the API's
configuration, not in this UI**. The pickers in a page's marking control and
in a space's access grants show exactly what is configured, and nothing else.
Adding a category or a value is a deployment change, on purpose: a vocabulary
that decides who may read what should arrive through a reviewed change, not
through a form.

If a page carries a selector from a category this instance has not configured,
nobody can read that page until the category is configured and someone is
granted the value. That is the safe direction, and it is how a page arriving
from another instance behaves too.

## Clearance, nationality and eligibility live in Keycloak

None of them is set in RocketWiki. A person's clearance, the countries they
hold nationality of, and whether they are eligible for a selector category are
attributes on their account in the identity provider, read afresh from their
token on every request. Change them there; the wiki notices at the next token
refresh. A space admin can grant access and selector values within a space, but
cannot make anyone cleared, eligible, or a national of anywhere.

## Sync status

For instances that exchange content across a boundary. Shows what has been
exported and what has been imported, per space and per origin, with the
sequence positions — a gap or a stalled position is the thing to look for.

Content crosses; **permissions do not**. Access grants, role grants and a
space's default page are decided locally by whichever instance holds the
replica. A page's marking does cross, selectors included: a page that is
`SECRET APPLE` on the sending side is `SECRET APPLE` wherever it lands. If the
receiving instance has no `FRUIT` category configured, that page is visible to
nobody there until it does — an unknown selector matches no one rather than
being dropped. An arriving page with no declared marking is treated as TOP
SECRET rather than guessed at.

## What an admin cannot do

Instance admin is not a clearance, and it is not an access grant. It grants no
access to content above your own clearance, no eligibility, and no visibility
into a space that has not granted you access — you see such a space's pages as
(protected) exactly as a space admin without access does, and pages above your
clearance are absent from your search results and your analytics totals exactly
as they are for anyone else. What you can do is change a grant, and that leaves
a row in the audit log.
