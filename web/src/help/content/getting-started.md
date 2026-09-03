# Getting started

RocketWiki is a wiki: **spaces** hold **pages**, pages hold text, and almost
everything else is a way of finding or organising those two things.

## Spaces

A space is a container with its own members and its own permissions —
typically one per team, project or subject area. The picker at the top of the
left sidebar switches between the spaces you can see.

You only ever see spaces you have a role in. A space you have no role in is not
shown as locked or greyed out; it simply is not there. That is deliberate
throughout RocketWiki: **things you cannot see are absent rather than
forbidden**, so the interface never becomes a way of discovering what exists.

## Pages

Below the space picker is that space's page tree. Pages nest, and the tree opens
to wherever you are, leaving the rest folded.

Every page has two addresses:

- `/spaces/ENG/launch-notes` — the readable one, made from the page's *slug*
- `/pages/{id}` — a permanent one that never changes

The readable address is the one to share. Because the hierarchy is deliberately
not part of it, moving a page around the tree never breaks a link to it.

## Writing

**Edit** opens the editor. It is a rich-text editor that saves Markdown, so
headings, lists, tables, links and code blocks all work the way you would
expect, and what is stored stays readable as plain text.

Saving takes you to the page. If someone else saved while you were editing, you
are told, shown what changed, and given the choice — your work is never
silently overwritten or silently discarded.

Several people can edit the same page at once. You will see their cursors.

## What changed, and who changed it

**History** on any page lists every save: the revision number, who pressed
save, when, and the summary they left. Where a save came out of a shared
editing session it also names the other people who typed into it, so a
co-edited revision is not credited to one person alone.

Pick any two revisions with the **From** and **To** columns and the changes
between them appear underneath — additions and removals marked line by line,
and word by word within a changed line. It reads oldest-to-newest whichever
order you picked them in.

History is open to anyone who can read the page. It shows you past states of
something already in front of you, so there is nothing there you could not
already see.

## Finding things

- **Search** (top of the screen) covers page titles and content.
- **Labels** tag pages; the space browser can filter the tree by one.
- **Ask** answers a question in prose, citing the pages it drew on.

All three only ever return things you are allowed to read.

## Your profile

Everyone who has signed in has a profile page, and anyone who is signed in can
see anyone's. Open your own from the account menu at the bottom of the left
sidebar, or a colleague's by following their name wherever it appears — on a
comment, an attachment, a page's history, or as the owner of a space.

A profile shows two things: the person's **clearance**, and for each selector
category whether they are **eligible** for it. That is what a colleague needs
before showing someone a page at a given level or in a given compartment, and
it is all the page shows — no email address, no nationality, no activity.

Both values are whatever the person's sign-in carried the last time they signed
in here, and both are managed in Keycloak rather than in RocketWiki: nothing on
the page can be edited, and a change made in Keycloak shows once the person
next signs in. A clearance that was never recorded shows as **Not recorded**
rather than as a level. An account that was created by sync and has never
signed in here has nothing to show yet, and says so.
