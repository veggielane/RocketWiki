import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import {
  REPLICA_EXPLANATION,
  describeAskUnavailable,
  describeAttachmentUnavailable,
  describeDiagramUnavailable,
  describeGitLabUnavailable,
  describeLoadFailure,
  describeNoPages,
  describeNoSpaces,
  describePageListUnavailable,
  describeWriteFailure,
  replicaBadgeLabel,
  type AttachmentUnavailableReason,
  type DiagramUnavailableReason,
  type LoadFailureReason,
  type PageListUnavailableReason,
  type WriteFailureReason,
} from '../unavailableCopy'
import { describeBlockedSubtree } from '../blockedSubtreeCopy'
import { describeDenialReason } from '../../access/permission/describeDenialReason'
import { describeSave } from '../../editor/describeSave'
import type { AskWikiUnavailableReason, GitLabUnavailableReason } from '../../graphql/generated/graphql'

/**
 * `design.md` is not shipped to an instance and is not linked from anywhere in
 * the app, so a section reference in rendered copy is noise at best and a
 * broken promise of documentation at worst — it points a reader at a document
 * they cannot open. Sixteen of them had accumulated across admin descriptions,
 * marking helper text, the replica explanation and a permission-denial reason.
 *
 * The reference is only forbidden in text a USER SEES. Code comments, JSDoc and
 * test names keep theirs: those are how this codebase stays tied to its
 * constitution, and they are exactly what a reader of the source needs.
 *
 * So this checks the produced STRINGS rather than grepping the files — the one
 * way to tell "shown to a user" from "explaining to a developer" without
 * guessing at comment syntax.
 */

const SECTION_REFERENCE = /§|design\.md/i

/** Every reason each copy module can be handed, so nothing goes unexercised. */
const GITLAB_REASONS: GitLabUnavailableReason[] = [
  'NOT_CONFIGURED',
  'NO_CREDENTIAL',
  'INVALID_CREDENTIAL',
  'NOT_FOUND',
  'UNREACHABLE',
]
const ASK_REASONS: (AskWikiUnavailableReason | 'REQUEST_FAILED')[] = [
  'NO_RESULTS',
  'UNREACHABLE',
  'NOT_CONFIGURED',
  'REQUEST_FAILED',
]
const ATTACHMENT_REASONS: AttachmentUnavailableReason[] = [
  'INLINE_IMAGE',
  { kind: 'DOWNLOAD_FAILED', fileName: 'spec.pdf' },
  { kind: 'UPLOAD_FAILED', fileName: 'spec.pdf' },
]
const LOAD_REASONS: LoadFailureReason[] = [
  'SPACE_LIST',
  'SPACE',
  'PAGE',
  'TRASH',
  'ARCHIVED_SPACES',
  'AUDIT_EVENTS',
  'EMOJI_REGISTRY',
  'PROPERTY_KEY_REGISTRY',
  'NATIONALITY_VOCABULARY',
  'SYNC_STATUS',
  'SEARCH',
  { kind: 'PERMISSION_INSPECTION', forPrincipal: true },
  { kind: 'PERMISSION_INSPECTION', forPrincipal: false },
]
const WRITE_REASONS: WriteFailureReason[] = ['PAGE_PROPERTY', 'PROPERTY_KEY', 'PAGE_MARKING']
const DIAGRAM_REASONS: DiagramUnavailableReason[] = ['MERMAID_SOURCE', 'DRAWIO_PAYLOAD']
const PAGE_LIST_REASONS: PageListUnavailableReason[] = ['REQUEST_FAILED', 'QUERY_INVALID']
const DENIAL_REASONS = [
  'no-space-role',
  'replica-read-only',
  'insufficient-space-role',
  'restriction:page-1:rule-9',
]

/** Every sentence the shared copy modules can put in front of a user. */
function allUserFacingCopy(): { source: string; text: string }[] {
  const out: { source: string; text: string }[] = []
  const add = (source: string, text: string | null | undefined) => {
    if (text) out.push({ source, text })
  }

  for (const r of GITLAB_REASONS) add(`describeGitLabUnavailable(${r})`, describeGitLabUnavailable(r).summary)
  for (const r of ASK_REASONS) add(`describeAskUnavailable(${r})`, describeAskUnavailable(r).summary)
  for (const r of ATTACHMENT_REASONS)
    add(`describeAttachmentUnavailable(${JSON.stringify(r)})`, describeAttachmentUnavailable(r).summary)
  for (const r of LOAD_REASONS) add(`describeLoadFailure(${JSON.stringify(r)})`, describeLoadFailure(r).summary)
  for (const r of WRITE_REASONS) add(`describeWriteFailure(${r})`, describeWriteFailure(r).summary)
  for (const r of DIAGRAM_REASONS) add(`describeDiagramUnavailable(${r})`, describeDiagramUnavailable(r).summary)
  for (const r of PAGE_LIST_REASONS) add(`describePageListUnavailable(${r})`, describePageListUnavailable(r).summary)
  for (const r of DENIAL_REASONS) add(`describeDenialReason(${r})`, describeDenialReason(r))

  add('REPLICA_EXPLANATION', REPLICA_EXPLANATION)
  add('replicaBadgeLabel(LOW)', replicaBadgeLabel('LOW'))
  add('replicaBadgeLabel(null)', replicaBadgeLabel(null))
  add('describeNoSpaces(admin)', describeNoSpaces(true))
  add('describeNoSpaces(reader)', describeNoSpaces(false))
  add('describeNoPages(canCreate)', describeNoPages(true))
  add('describeNoPages(reader)', describeNoPages(false))
  add('describeBlockedSubtree(deleted)', describeBlockedSubtree(3, 'deleted'))
  add('describeBlockedSubtree(included)', describeBlockedSubtree(1, 'included'))
  add('describeSave(manual)', describeSave({ revisionNumber: 12, contributors: ['Ada'], auto: false }))
  add('describeSave(auto)', describeSave({ revisionNumber: 12, contributors: [], auto: true }))

  return out
}

describe('shared copy modules never cite the design document', () => {
  it('produces no string containing a § or design.md reference', () => {
    const offenders = allUserFacingCopy()
      .filter(({ text }) => SECTION_REFERENCE.test(text))
      .map(({ source, text }) => `${source} → ${text}`)

    expect(offenders, 'user-facing copy must say the meaning, not cite the section').toEqual([])
  })

  it('exercised every module, so an empty pass cannot be a vacuous one', () => {
    // A refactor that renamed a reason would otherwise silently shrink the set
    // this walks, and the test above would keep passing over nothing.
    expect(allUserFacingCopy().length).toBeGreaterThanOrEqual(40)
  })

  it('still says why a replica cannot be edited, rather than dropping the sentence', () => {
    // Removing the citation must not remove the explanation it was attached to.
    expect(describeDenialReason('replica-read-only')).toMatch(/one-way sync/i)
    expect(describeDenialReason('replica-read-only')).toMatch(/whatever grants/i)
    expect(REPLICA_EXPLANATION).toMatch(/origin instance/i)
  })
})

describe('in-app help never cites the design document', () => {
  // The help topics ARE the app's documentation (/-/docs). Read off disk
  // because they are plain Markdown bundled with `?raw`, which Vitest's
  // transform does not hand back as a module here.
  const contentDir = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'help', 'content')

  it('has help topics to check', () => {
    expect(readdirSync(contentDir).filter((f) => f.endsWith('.md')).length).toBeGreaterThan(0)
  })

  for (const file of readdirSync(contentDir).filter((f) => f.endsWith('.md'))) {
    it(`${file} says the meaning rather than citing a section`, () => {
      const markdown = readFileSync(join(contentDir, file), 'utf8')
      const offending = markdown
        .split('\n')
        .map((line, i) => ({ line, n: i + 1 }))
        .filter(({ line }) => SECTION_REFERENCE.test(line))
        .map(({ line, n }) => `${file}:${n}: ${line.trim()}`)

      expect(offending).toEqual([])
    })
  }
})
