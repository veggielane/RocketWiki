import type { SvgIconComponent } from '@mui/icons-material'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import MenuBookOutlinedIcon from '@mui/icons-material/MenuBookOutlined'
import StickyNote2OutlinedIcon from '@mui/icons-material/StickyNote2Outlined'
import ChecklistOutlinedIcon from '@mui/icons-material/ChecklistOutlined'
import CalendarMonthOutlinedIcon from '@mui/icons-material/CalendarMonthOutlined'
import InsertChartOutlinedIcon from '@mui/icons-material/InsertChartOutlined'
import StorageOutlinedIcon from '@mui/icons-material/StorageOutlined'
import CodeOutlinedIcon from '@mui/icons-material/CodeOutlined'
import TerminalOutlinedIcon from '@mui/icons-material/TerminalOutlined'
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined'
import ScienceOutlinedIcon from '@mui/icons-material/ScienceOutlined'
import RocketLaunchOutlinedIcon from '@mui/icons-material/RocketLaunchOutlined'
import BuildOutlinedIcon from '@mui/icons-material/BuildOutlined'
import CloudOutlinedIcon from '@mui/icons-material/CloudOutlined'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined'
import LightbulbOutlinedIcon from '@mui/icons-material/LightbulbOutlined'
import PeopleOutlinedIcon from '@mui/icons-material/PeopleOutlined'
import MapOutlinedIcon from '@mui/icons-material/MapOutlined'
import StarOutlineOutlinedIcon from '@mui/icons-material/StarOutlineOutlined'
import FlagOutlinedIcon from '@mui/icons-material/FlagOutlined'
import type { PageIcon } from '../graphql/generated/graphql'

export interface PageIconEntry {
  /** The wire value — a `PageIcon` member of schema.graphql's enum. */
  value: PageIcon
  /**
   * Names the icon in the picker. Not an alt text: everywhere a page icon is
   * rendered, the page's title sits beside it, so the glyph is decorative and
   * a screen reader would otherwise read the page twice.
   */
  label: string
  Icon: SvgIconComponent
}

/**
 * The one place a `PageIcon` becomes something on screen. A fixed, curated,
 * build-time set — deliberately not the custom-emoji registry (design.md §19),
 * which is instance-admin content that syncs as literal text: an icon is a
 * server-side enum that gates nothing and travels in sync bundles as a name,
 * so it must mean the same thing on every instance or it means nothing.
 *
 * Outlined variants throughout, matching the chrome the trees already draw
 * (`ArticleOutlined`, `FolderOutlined`) — a filled glyph beside them reads as
 * a different weight of thing rather than the same row's icon.
 *
 * Typed as an exhaustive `Record<PageIcon, …>` so a member added to the schema
 * enum fails this build instead of rendering a blank slot in the tree; the
 * declaration order below is the schema's, and it is also the picker's order,
 * since object key order is the insertion order for string keys.
 */
const ICONS: Record<PageIcon, Omit<PageIconEntry, 'value'>> = {
  DOCUMENT: { label: 'Document', Icon: ArticleOutlinedIcon },
  BOOK: { label: 'Book', Icon: MenuBookOutlinedIcon },
  NOTE: { label: 'Note', Icon: StickyNote2OutlinedIcon },
  CHECKLIST: { label: 'Checklist', Icon: ChecklistOutlinedIcon },
  CALENDAR: { label: 'Calendar', Icon: CalendarMonthOutlinedIcon },
  CHART: { label: 'Chart', Icon: InsertChartOutlinedIcon },
  DATABASE: { label: 'Database', Icon: StorageOutlinedIcon },
  CODE: { label: 'Code', Icon: CodeOutlinedIcon },
  TERMINAL: { label: 'Terminal', Icon: TerminalOutlinedIcon },
  BUG: { label: 'Bug', Icon: BugReportOutlinedIcon },
  FLASK: { label: 'Flask', Icon: ScienceOutlinedIcon },
  ROCKET: { label: 'Rocket', Icon: RocketLaunchOutlinedIcon },
  WRENCH: { label: 'Wrench', Icon: BuildOutlinedIcon },
  CLOUD: { label: 'Cloud', Icon: CloudOutlinedIcon },
  LOCK: { label: 'Lock', Icon: LockOutlinedIcon },
  SHIELD: { label: 'Shield', Icon: ShieldOutlinedIcon },
  WARNING: { label: 'Warning', Icon: WarningAmberOutlinedIcon },
  LIGHTBULB: { label: 'Lightbulb', Icon: LightbulbOutlinedIcon },
  PEOPLE: { label: 'People', Icon: PeopleOutlinedIcon },
  MAP: { label: 'Map', Icon: MapOutlinedIcon },
  STAR: { label: 'Star', Icon: StarOutlineOutlinedIcon },
  FLAG: { label: 'Flag', Icon: FlagOutlinedIcon },
}

/** Every icon this build can draw, in the order the picker offers them. */
export const PAGE_ICON_OPTIONS: readonly PageIconEntry[] = (Object.keys(ICONS) as PageIcon[]).map((value) => ({
  value,
  ...ICONS[value],
}))

const BY_VALUE = new Map<string, PageIconEntry>(PAGE_ICON_OPTIONS.map((entry) => [entry.value, entry]))

/**
 * The entry for a page's icon, or `undefined` when it has none — and, on
 * purpose, also when it names one this build has never heard of.
 *
 * That second case is real rather than defensive: a page arriving in a sync
 * bundle from an instance running a newer icon set (design.md §12) carries the
 * name it was written with. The server already maps a name it doesn't know to
 * null on import, and a client one release behind its API is the same
 * situation, so the caller degrades to its fallback glyph instead of throwing
 * or drawing a hole. Takes a plain `string` for exactly that reason — the
 * generated `PageIcon` union is this build's opinion, not the wire's.
 */
export function lookupPageIcon(value: string | null | undefined): PageIconEntry | undefined {
  return value == null ? undefined : BY_VALUE.get(value)
}
