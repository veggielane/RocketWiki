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
import {
  ABOVE_CLEARANCE_REASON,
  CLASSIFICATION_LADDER,
  NOT_GRANTED_REASON,
  canonicalCountries,
  describeMarkingRefusal,
  levelIsWithinClearance,
  markingRefusal,
  notEligibleReason,
  selectorEligible,
  selectorGranted,
  type SelectorValue,
  type ViewerClearance,
} from './clearance'
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
 * **It prevents rather than refuses.** §21.6's rule — no resulting marking
 * you could not then read — is mirrored from `me.clearance`,
 * `me.nationality`, `me.selectorEligibility` and the space's
 * `viewerSelectorGrants` through markings/clearance.ts, so an unavailable
 * level, category or value is visibly unavailable with its reason attached
 * instead of being offered and rejected. The server is still the authority:
 * a refusal that arrives anyway (a clearance or a grant changed mid-session,
 * say) is shown as the server sent it, never swallowed.
 *
 * The vocabulary is all the server's: level spellings from the scheme,
 * caveat countries pinned to the schema's enum, selector categories and
 * values from instance configuration. Nothing here is typed by hand.
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
  // §21.1: the levels with their DISPLAY spellings. A picker holds no marking,
  // so it cannot use `levelName` — it must name all four before one is chosen,
  // and spelling them here would be the second implementation of the display
  // form that §21.1's "one method each" exists to prevent.
  const [{ data: schemeData }] = useClassificationSchemeQuery({ pause: !canEdit })
  // §21.15: the categories and values this instance configures — the whole
  // selector vocabulary. Empty is the ordinary state on an instance without
  // selectors, and then there is simply nothing to pick.
  const [{ data: categoriesData, error: categoriesError }] = useSelectorCategoriesQuery({ pause: !canEdit })
  // The values the caller is GRANTED in this space (§6.4's union over their
  // access grants) — the per-space half of the selector gate.
  const [{ data: grantsData, error: grantsError }] = useSpaceSelectorGrantsQuery({
    variables: { key: spaceKey },
    pause: !canEdit,
  })

  // Spelling from the server, ORDER from the ladder — deliberately not from
  // the returned array, even though the server returns scheme order. The order
  // options render in must be the same order the greying-out comparison uses
  // (clearance.ts), or an option could sit above another while claiming to be
  // below it. The ladder is compile-time exhaustive, so every level renders
  // even if this query fails; a level the scheme did not name falls back to its
  // wire name, which is ugly but never wrong.
  const levelOptions = CLASSIFICATION_LADDER.map((level) => ({
    level,
    name: schemeData?.classificationScheme.find((entry) => entry.level === level)?.name ?? level,
  }))

  // Null grants while the read has not answered: the comparison then claims
  // no grant refusal (clearance.ts), and the server decides.
  const grants: readonly SelectorValue[] | null = grantsData?.space?.viewerSelectorGrants ?? null
  const viewer: ViewerClearance | null = meData?.me
    ? {
        clearance: meData.me.clearance,
        nationality: meData.me.nationality,
        selectorEligibility: meData.me.selectorEligibility,
        selectorGrants: grants,
      }
    : null

  const categories = categoriesData?.selectorCategories ?? []
  const stored = draftFrom(marking)
  // A caveat token stored before the vocabulary was fixed cannot be sent back
  // (the input is an enum), so it cannot be kept either: saving replaces the
  // caveat with what the picker holds. Said up front rather than discovered.
  const legacyCaveat = canonicalCountries(marking.eyesOnly).filter((value) => !isNationalCaveatCountry(value))

  const refusal = markingRefusal(
    { level: draft.level, eyesOnly: draft.eyesOnly, selectors: selectorList(draft.selectors) },
    viewer,
  )
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
        A UK Government classification, optional selectors, an optional eyes-only caveat, and a UK prefix. The
        classification, the selectors and the eyes-only caveat decide who may read this page; the prefix does
        not. This is not one of the key/value properties below — it is a single value, replaced as a whole.
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
          <FormControl>
            <FormLabel id="marking-level-label">Classification</FormLabel>
            <RadioGroup
              aria-labelledby="marking-level-label"
              value={draft.level}
              onChange={(e) => setDraft((prev) => ({ ...prev, level: e.target.value as ClassificationLevel }))}
            >
              {levelOptions.map(({ level, name }) => {
                // §21.6: a level above your own clearance is a marking you
                // could not then read, so it is never offered — the reason
                // travels with the option instead of arriving after a refusal.
                const unavailable = viewer !== null && !levelIsWithinClearance(level, viewer.clearance)
                return (
                  <FormControlLabel
                    key={level}
                    value={level}
                    disabled={unavailable || busy}
                    control={<Radio size="small" />}
                    // MUI greys a disabled label to `text.disabled`, which is
                    // below 4.5:1 — and the reason a level is unavailable is
                    // exactly the text a user needs to be able to read.
                    sx={{ '& .MuiFormControlLabel-label.Mui-disabled': { color: 'text.secondary' } }}
                    label={
                      <Box component="span" sx={{ display: 'inline-flex', alignItems: 'baseline', gap: 1 }}>
                        <Box component="span" sx={{ fontWeight: 600 }}>
                          {name}
                        </Box>
                        {unavailable && (
                          <>
                            {/* An explicit space: both spans are inline, so
                                without it the accessible name concatenates to
                                "OFFICIAL_SENSITIVEAbove your clearance". The
                                flex gap swallows it visually. */}
                            {' '}
                            <Typography component="span" variant="caption" color="text.secondary">
                              {ABOVE_CLEARANCE_REASON}
                            </Typography>
                          </>
                        )}
                      </Box>
                    }
                  />
                )
              })}
            </RadioGroup>
          </FormControl>

          {categoriesError !== undefined && (
            <Alert severity="info">{describeLoadFailure('SELECTOR_CATEGORIES').summary}</Alert>
          )}
          {grantsError !== undefined && <Alert severity="info">{describeLoadFailure('SELECTOR_GRANTS').summary}</Alert>}

          {categories.length > 0 && (
            <FormControl component="fieldset" sx={{ display: 'block' }}>
              <FormLabel component="legend">Selectors</FormLabel>
              <FormHelperText sx={{ ml: 0, mb: 1 }}>
                At most one value per category. A reader must be eligible for the category and granted the value
                in this space.
              </FormHelperText>
              <Stack spacing={1.5}>
                {categories.map((category) => {
                  const current = draft.selectors[category.name] ?? null
                  const storedValue = stored.selectors[category.name] ?? null
                  // §21.15's eligibility gate: a category this caller's token
                  // does not qualify them for is a marking they could not then
                  // read, whatever value it held — so the whole picker is
                  // unavailable, with the reason where the values would be.
                  const eligible = viewer === null || selectorEligible(category.name, viewer.selectorEligibility)
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
                      disabled={busy || !eligible}
                      onChange={(e) =>
                        setDraft((prev) => ({
                          ...prev,
                          selectors: {
                            ...prev.selectors,
                            [category.name]: e.target.value === NO_VALUE ? null : e.target.value,
                          },
                        }))
                      }
                      helperText={
                        !eligible
                          ? notEligibleReason(category.name)
                          : (category.description ?? 'None means this page carries no selector in this category.')
                      }
                      // Same reason as the level labels: the reason a picker is
                      // unavailable has to stay readable.
                      sx={{ maxWidth: 420, '& .MuiFormHelperText-root.Mui-disabled': { color: 'text.secondary' } }}
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
