import { useState } from 'react'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  FormControl,
  FormControlLabel,
  FormHelperText,
  FormLabel,
  MenuItem,
  Paper,
  Radio,
  RadioGroup,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import {
  useClassificationSchemeQuery,
  useCurrentUserQuery,
  useSelectorCategoriesQuery,
  useSetPageMarkingMutation,
  useSpaceSelectorGrantsQuery,
  type ClassificationLevel,
  type NationalCaveatCountry,
  type PageMarkingFragment,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, describeWriteFailure } from '../feedback/unavailableCopy'
import { NATIONAL_CAVEAT_COUNTRIES, isNationalCaveatCountry } from './caveatCountries'
import { LEVEL_IS_DISPLAY_NOTE, MARKING_SECTION_DESCRIPTION, SELECTOR_RULE_NOTE } from './markingCopy'
import {
  NOT_GRANTED_REASON,
  canonicalCountries,
  describeMarkingRefusal,
  markingRefusal,
  selectorGranted,
  type MarkingViewer,
  type SelectorValue,
} from './markingRefusal'
import { MarkingBanner } from './MarkingBanner'

export interface PageMarkingSectionProps {
  pageId: string
  /** The page's space — the selector values the caller is GRANTED are per space (`Space.viewerSelectorGrants`). */
  spaceKey: string
  marking: PageMarkingFragment
  /** design.md §21.6: `setPageMarking` requires canEdit on this page, so a viewer gets the read-only read-out. */
  canEdit: boolean
  onFeedback: (feedback: { notice: string | null; error: string | null }) => void
  onReplicaRefusal: (originInstanceId: string) => void
  onChanged: () => void
}

/**
 * §21.6 replaces the whole marking, so the draft is the whole marking too —
 * a partial edit has no meaning here. `selectors` is keyed by category with
 * at most one value each (§21.15): null is "no selector in this category",
 * and the shape itself makes a second value in one category unrepresentable.
 */
interface MarkingDraft {
  level: ClassificationLevel
  eyesOnly: NationalCaveatCountry[]
  ukPrefix: boolean
  selectors: Record<string, string | null>
}

/**
 * The "no value" choice. A sentinel rather than `''` for the same reason the
 * icon picker uses one: MUI reads an empty Select value as unfilled and leaves
 * the floating label sitting on the option text. Lowercase and bracketed so it
 * can never collide with a value, which the catalog keeps upper-case.
 */
const NO_VALUE = '__none__'

/** The stored caveat, restricted to what the mutation can send back. Legacy tokens are reported separately (see `legacyCaveat`). */
function sendableCaveat(values: readonly string[]): NationalCaveatCountry[] {
  return canonicalCountries(values).filter(isNationalCaveatCountry)
}

function draftFrom(marking: PageMarkingFragment): MarkingDraft {
  return {
    level: marking.level,
    eyesOnly: sendableCaveat(marking.eyesOnly),
    ukPrefix: marking.ukPrefix,
    selectors: Object.fromEntries(marking.selectors.map((selector) => [selector.category, selector.value])),
  }
}

function selectorList(selectors: Record<string, string | null>): SelectorValue[] {
  return Object.entries(selectors)
    .filter((entry): entry is [string, string] => entry[1] !== null)
    .map(([category, value]) => ({ category, value }))
}

function selectorKey(selectors: readonly SelectorValue[]): string {
  return selectors
    .map((selector) => `${selector.category}=${selector.value}`)
    .sort()
    .join('\0')
}

function isDirty(draft: MarkingDraft, marking: PageMarkingFragment): boolean {
  const stored = draftFrom(marking)
  return (
    draft.level !== stored.level ||
    canonicalCountries(draft.eyesOnly).join('\0') !== stored.eyesOnly.join('\0') ||
    draft.ukPrefix !== stored.ukPrefix ||
    selectorKey(selectorList(draft.selectors)) !== selectorKey(selectorList(stored.selectors))
  )
}

/**
 * The marking control (design.md §21). It lives on the page-properties screen
 * because that is where "everything about this page that is not its text"
 * already is — but it is deliberately its own section above the key/value
 * table and never a row in it: a property is admin-defined metadata beside
 * the page (§20), while a marking gates who may read the page at all.
 *
 * **It prevents rather than refuses.** The server refuses a marking its
 * author could not then read — a selector value they are not granted in this
 * space, or an eyes-only set holding none of their own nationalities — and
 * both are mirrored from `me.nationality` and the space's
 * `viewerSelectorGrants` through markings/markingRefusal.ts, so an
 * unavailable value is visibly unavailable with its reason attached instead
 * of being offered and rejected. The server is still the authority: a
 * refusal that arrives anyway (a grant changed mid-session, say) is shown as
 * the server sent it, never swallowed.
 *
 * **Every level is offered, to everyone.** This deployment carries no
 * clearance attribute, so the classification is display — compared against
 * nobody, exactly like the UK prefix — and any editor may set any level.
 * Nothing here greys a level, and nothing should: a level check with no
 * server gate behind it could only ever disagree with the server.
 *
 * The vocabulary is all the server's: level spellings and their order from
 * the scheme, caveat countries pinned to the schema's enum, selector
 * categories and values from instance configuration. Nothing here is typed
 * by hand.
 */
export function PageMarkingSection({
  pageId,
  spaceKey,
  marking,
  canEdit,
  onFeedback,
  onReplicaRefusal,
  onChanged,
}: PageMarkingSectionProps) {
  const [, setPageMarking] = useSetPageMarkingMutation()
  const [draft, setDraft] = useState<MarkingDraft>(() => draftFrom(marking))
  const [busy, setBusy] = useState(false)

  // Every read here exists only to shape the editing affordances, so a viewer
  // pays for none of them.
  const [{ data: meData }] = useCurrentUserQuery({ pause: !canEdit })
  // §21.1: the levels with their DISPLAY spellings, in scheme order. A picker
  // holds no marking, so it cannot use `levelName` — it must name all four
  // before one is chosen — and spelling or ordering them here would be the
  // second implementation of the display form that §21.1's "one method each"
  // exists to prevent. The scheme is the only source: nothing client-side
  // ranks a level, because nothing compares one against a person.
  const [{ data: schemeData, error: schemeError }] = useClassificationSchemeQuery({ pause: !canEdit })
  // §21.15: the categories and values this instance configures — the whole
  // selector vocabulary. Empty is the ordinary state on an instance without
  // selectors, and then there is simply nothing to pick.
  const [{ data: categoriesData, error: categoriesError }] = useSelectorCategoriesQuery({ pause: !canEdit })
  // The values the caller is GRANTED in this space (§6.4's union over their
  // access grants) — the whole of the selector gate.
  const [{ data: grantsData, error: grantsError }] = useSpaceSelectorGrantsQuery({
    variables: { key: spaceKey },
    pause: !canEdit,
  })

  // Spelling AND order from the server, as returned. The page's own level
  // stays offered even when the scheme does not name it (a server ahead of
  // this client), under the server's own spelling for it — so what the page
  // carries can be seen and kept, never silently dropped. Until the scheme
  // has answered there is nothing to offer; a failed read says so below.
  const schemeLevels = schemeData?.classificationScheme
  const levelOptions =
    schemeLevels === undefined
      ? []
      : schemeLevels.some((entry) => entry.level === marking.level)
        ? schemeLevels
        : [...schemeLevels, { level: marking.level, name: marking.levelName }]

  // Null grants while the read has not answered: the comparison then claims
  // no grant refusal (markingRefusal.ts), and the server decides.
  const grants: readonly SelectorValue[] | null = grantsData?.space?.viewerSelectorGrants ?? null
  const viewer: MarkingViewer | null = meData?.me ? { nationality: meData.me.nationality, selectorGrants: grants } : null

  const categories = categoriesData?.selectorCategories ?? []
  const stored = draftFrom(marking)
  // A caveat token stored before the vocabulary was fixed cannot be sent back
  // (the input is an enum), so it cannot be kept either: saving replaces the
  // caveat with what the picker holds. Said up front rather than discovered.
  const legacyCaveat = canonicalCountries(marking.eyesOnly).filter((value) => !isNationalCaveatCountry(value))

  const refusal = markingRefusal({ eyesOnly: draft.eyesOnly, selectors: selectorList(draft.selectors) }, viewer)
  const dirty = isDirty(draft, marking)

  const handleSave = async () => {
    if (!dirty || busy || refusal) return
    onFeedback({ notice: null, error: null })
    setBusy(true)
    try {
      const result = await setPageMarking({
        input: {
          pageId,
          level: draft.level,
          eyesOnly: sendableCaveat(draft.eyesOnly),
          selectors: selectorList(draft.selectors),
          // Every part, explicitly, every time: the input has no defaults, so
          // there is nothing an omission could silently re-assert (§21.12).
          ukPrefix: draft.ukPrefix,
        },
      })
      if (result.error !== undefined) {
        onFeedback({ notice: null, error: describeWriteFailure('PAGE_MARKING').summary })
        return
      }
      const payload = result.data?.setPageMarking
      const replica = asReadOnlyReplica(payload?.error)
      if (replica) {
        onReplicaRefusal(replica.originInstanceId ?? 'its origin instance')
        return
      }
      const refused = describeMutationError(payload?.error)
      if (refused) {
        onFeedback({ notice: null, error: refused })
        return
      }
      // The server's own label, not a locally assembled one (§21.4).
      onFeedback({ notice: `Marking set to ${payload?.marking?.label ?? 'the new value'}.`, error: null })
    } finally {
      setBusy(false)
      onChanged()
    }
  }

  return (
    <Paper variant="outlined" sx={{ p: 2, maxWidth: 880 }}>
      <Typography variant="h6" component="h2">
        Protective marking
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, mb: 2 }}>
        {MARKING_SECTION_DESCRIPTION}
      </Typography>

      <Box sx={{ mb: 2 }}>
        <MarkingBanner label={marking.label} level={marking.level} placement="section" />
      </Box>

      {!canEdit && (
        <Typography variant="body2" color="text.secondary">
          Only someone who can edit this page can change its marking.
        </Typography>
      )}

      {canEdit && (
        <Stack spacing={2}>
          {schemeError !== undefined && (
            <Alert severity="info">{describeLoadFailure('CLASSIFICATION_SCHEME').summary}</Alert>
          )}

          <FormControl>
            <FormLabel id="marking-level-label">Classification</FormLabel>
            <RadioGroup
              aria-labelledby="marking-level-label"
              value={draft.level}
              onChange={(e) => setDraft((prev) => ({ ...prev, level: e.target.value as ClassificationLevel }))}
            >
              {levelOptions.map(({ level, name }) => (
                // Every level, to everyone: there is no clearance in this
                // deployment for a level to sit above, so none is greyed and
                // none carries a reason.
                <FormControlLabel
                  key={level}
                  value={level}
                  disabled={busy}
                  control={<Radio size="small" />}
                  label={
                    <Box component="span" sx={{ fontWeight: 600 }}>
                      {name}
                    </Box>
                  }
                />
              ))}
            </RadioGroup>
            <FormHelperText sx={{ ml: 0 }}>{LEVEL_IS_DISPLAY_NOTE}</FormHelperText>
          </FormControl>

          {categoriesError !== undefined && (
            <Alert severity="info">{describeLoadFailure('SELECTOR_CATEGORIES').summary}</Alert>
          )}
          {grantsError !== undefined && <Alert severity="info">{describeLoadFailure('SELECTOR_GRANTS').summary}</Alert>}

          {categories.length > 0 && (
            <FormControl component="fieldset" sx={{ display: 'block' }}>
              <FormLabel component="legend">Selectors</FormLabel>
              <FormHelperText sx={{ ml: 0, mb: 1 }}>{SELECTOR_RULE_NOTE}</FormHelperText>
              <Stack spacing={1.5}>
                {categories.map((category) => {
                  const current = draft.selectors[category.name] ?? null
                  const storedValue = stored.selectors[category.name] ?? null
                  // The page's own value stays offered even where the catalog
                  // no longer lists it, so what the page carries can be seen
                  // and cleared.
                  const values = storedValue && !category.values.includes(storedValue)
                    ? [...category.values, storedValue]
                    : category.values
                  return (
                    <TextField
                      key={category.name}
                      select
                      size="small"
                      label={category.name}
                      value={current ?? NO_VALUE}
                      disabled={busy}
                      onChange={(e) =>
                        setDraft((prev) => ({
                          ...prev,
                          selectors: {
                            ...prev.selectors,
                            [category.name]: e.target.value === NO_VALUE ? null : e.target.value,
                          },
                        }))
                      }
                      helperText={category.description ?? 'None means this page carries no selector in this category.'}
                      sx={{ maxWidth: 420 }}
                    >
                      <MenuItem value={NO_VALUE}>None</MenuItem>
                      {values.map((value) => {
                        // §21.15's grant gate: a value no access grant confers
                        // on this caller here is greyed with its reason. The
                        // page's current value is always offered — it is what
                        // the field shows — and unknown grants (the read has
                        // not answered) grey nothing; the server decides.
                        const granted =
                          value === storedValue ||
                          grants === null ||
                          selectorGranted({ category: category.name, value }, grants)
                        return (
                          <MenuItem key={value} value={value} disabled={!granted}>
                            {value}
                            {!granted && (
                              <Typography component="span" variant="caption" color="text.secondary" sx={{ ml: 1 }}>
                                {NOT_GRANTED_REASON}
                              </Typography>
                            )}
                          </MenuItem>
                        )
                      })}
                    </TextField>
                  )
                })}
              </Stack>
            </FormControl>
          )}

          {legacyCaveat.length > 0 && (
            <Alert severity="info">
              This marking names {legacyCaveat.join(', ')}, which an eyes-only caveat can no longer carry. Saving
              replaces the caveat with what is picked below.
            </Alert>
          )}

          <Autocomplete
            multiple
            size="small"
            disabled={busy}
            // §21.4: the fixed five, pinned to the schema's enum — not a
            // registry, not an ISO list.
            options={NATIONAL_CAVEAT_COUNTRIES}
            value={draft.eyesOnly}
            onChange={(_event, value) => setDraft((prev) => ({ ...prev, eyesOnly: [...value] }))}
            renderValue={(value, getItemProps) =>
              value.map((country, index) => (
                <Chip size="small" label={country} {...getItemProps({ index })} key={country} />
              ))
            }
            renderInput={(params) => (
              <TextField
                {...params}
                label="Eyes only"
                error={refusal?.kind === 'EYES_ONLY_EXCLUDES_YOU'}
                helperText="Leave empty for no caveat. A reader must hold one of these nationalities to view the page."
              />
            )}
          />

          <FormControl>
            <FormControlLabel
              control={
                <Switch
                  checked={draft.ukPrefix}
                  disabled={busy}
                  onChange={(e) => setDraft((prev) => ({ ...prev, ukPrefix: e.target.checked }))}
                />
              }
              label="UK prefix"
            />
            <FormHelperText sx={{ ml: 0 }}>Presentational only. It grants and denies nothing.</FormHelperText>
          </FormControl>

          {refusal && <Alert severity="warning">{describeMarkingRefusal(refusal)}</Alert>}

          <Box>
            <Button variant="contained" disabled={!dirty || busy || refusal !== null} onClick={() => void handleSave()}>
              Save marking
            </Button>
          </Box>
        </Stack>
      )}
    </Paper>
  )
}
