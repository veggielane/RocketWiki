import { useId, useState } from 'react'
import { Box, Stack, Typography, useTheme } from '@mui/material'

export interface ActivityDatum {
  day: string
  views: number
  edits: number
}

/**
 * Two-series activity over time, drawn as inline SVG.
 *
 * Hand-drawn rather than pulled from a chart library on purpose. The library this
 * template's design leans on (`@mui/x-charts`) is not a dependency here and adding
 * one for a single two-series line would be a large surface to maintain and patch
 * for one screen. What that costs — axes, ticks, tooltips — is written out below,
 * once.
 *
 * The palette is not a taste decision: the two series are validated against this
 * app's real light and dark paper for lightness band, chroma, colour-vision
 * separation and contrast, and both modes pass every check. They are also never the
 * only signal — the legend names both series and each line carries an end dot, so
 * identity survives a reader who cannot separate blue from orange.
 */

/** Slot 1 and slot 2 of the categorical palette, stepped per mode. Not `theme.palette.primary`:
 * a brand colour is chosen to sit under white text on a button, which is a different
 * job from sitting beside another series on a chart surface. */
const SERIES = {
  light: { views: '#2a78d6', edits: '#eb6834' },
  dark: { views: '#3987e5', edits: '#d95926' },
}

const HEIGHT = 220
const PAD = { top: 12, right: 16, bottom: 26, left: 44 }

export function ActivityChart({ data }: { data: readonly ActivityDatum[] }) {
  const theme = useTheme()
  const clipId = useId()
  const [hover, setHover] = useState<number | null>(null)
  const colours = theme.palette.mode === 'dark' ? SERIES.dark : SERIES.light

  if (data.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        No activity in this period.
      </Typography>
    )
  }

  const width = 720
  const plotWidth = width - PAD.left - PAD.right
  const plotHeight = HEIGHT - PAD.top - PAD.bottom
  // A single shared scale for both series. Two y-axes would let the reader compare
  // two things that are not comparable, which is the most common way a chart lies.
  const peak = Math.max(1, ...data.map((d) => Math.max(d.views, d.edits)))
  const x = (i: number) => PAD.left + (data.length === 1 ? plotWidth / 2 : (i / (data.length - 1)) * plotWidth)
  const y = (value: number) => PAD.top + plotHeight - (value / peak) * plotHeight

  const path = (pick: (d: ActivityDatum) => number) =>
    data.map((d, i) => `${i === 0 ? 'M' : 'L'} ${x(i).toFixed(1)} ${y(pick(d)).toFixed(1)}`).join(' ')
  const area = (pick: (d: ActivityDatum) => number) =>
    `${path(pick)} L ${x(data.length - 1).toFixed(1)} ${PAD.top + plotHeight} L ${x(0).toFixed(1)} ${PAD.top + plotHeight} Z`

  // Four ticks, at whole numbers — a fractional "0.5 views" is not a thing.
  const ticks = Array.from({ length: 4 }, (_, i) => Math.round((peak / 3) * i)).filter(
    (value, i, all) => all.indexOf(value) === i,
  )
  const active = hover !== null ? data[hover] : undefined

  return (
    <Stack spacing={1}>
      {/* The legend is always present with two series: colour is never the only
          channel carrying identity (WCAG 1.4.1). */}
      <Stack direction="row" spacing={2} sx={{ pl: `${PAD.left}px` }}>
        {[
          { label: 'Views', colour: colours.views },
          { label: 'Edits', colour: colours.edits },
        ].map((series) => (
          <Stack key={series.label} direction="row" spacing={0.75} sx={{ alignItems: "center" }}>
            <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: series.colour }} />
            <Typography variant="caption" color="text.secondary">
              {series.label}
            </Typography>
          </Stack>
        ))}
      </Stack>

      <Box sx={{ position: 'relative' }}>
        <Box
          component="svg"
          viewBox={`0 0 ${width} ${HEIGHT}`}
          role="img"
          aria-label={`Activity over ${data.length} days. ${data.reduce((n, d) => n + d.views, 0)} views and ${data.reduce((n, d) => n + d.edits, 0)} edits in total. The table below lists every day.`}
          sx={{ width: '100%', height: 'auto', display: 'block', overflow: 'visible' }}
          onMouseLeave={() => setHover(null)}
        >
          <defs>
            <clipPath id={clipId}>
              <rect x={PAD.left} y={PAD.top} width={plotWidth} height={plotHeight} />
            </clipPath>
          </defs>

          {/* Gridlines: hairline, solid, one step off the surface. Recessive on
              purpose — the data is the only thing allowed to be loud. */}
          {ticks.map((value) => (
            <g key={value}>
              <line
                x1={PAD.left}
                x2={width - PAD.right}
                y1={y(value)}
                y2={y(value)}
                stroke={theme.palette.divider}
                strokeWidth={1}
              />
              <text
                x={PAD.left - 8}
                y={y(value) + 4}
                textAnchor="end"
                fontSize={11}
                fill={theme.palette.text.secondary}
              >
                {value}
              </text>
            </g>
          ))}

          <g clipPath={`url(#${clipId})`}>
            {/* A wash, never a saturated block — the fill hints at volume, the line
                carries the value. */}
            <path d={area((d) => d.views)} fill={colours.views} opacity={0.1} />
            <path d={path((d) => d.views)} fill="none" stroke={colours.views} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
            <path d={path((d) => d.edits)} fill="none" stroke={colours.edits} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
          </g>

          {/* End dots carry a surface-coloured ring so they stay legible where the
              two series cross. */}
          {(['views', 'edits'] as const).map((key) => (
            <circle
              key={key}
              cx={x(data.length - 1)}
              cy={y(data[data.length - 1]![key])}
              r={4}
              fill={colours[key]}
              stroke={theme.palette.background.paper}
              strokeWidth={2}
            />
          ))}

          {active && hover !== null && (
            <line
              x1={x(hover)}
              x2={x(hover)}
              y1={PAD.top}
              y2={PAD.top + plotHeight}
              stroke={theme.palette.text.secondary}
              strokeWidth={1}
            />
          )}

          {/* Hit targets are full-height bands, so pointing anywhere in a day's
              column works — a 4px dot is not a target. */}
          {data.map((d, i) => (
            <rect
              key={d.day}
              x={x(i) - plotWidth / Math.max(1, data.length - 1) / 2}
              y={PAD.top}
              width={plotWidth / Math.max(1, data.length - 1)}
              height={plotHeight}
              fill="transparent"
              onMouseEnter={() => setHover(i)}
            />
          ))}

          <line
            x1={PAD.left}
            x2={width - PAD.right}
            y1={PAD.top + plotHeight}
            y2={PAD.top + plotHeight}
            stroke={theme.palette.divider}
            strokeWidth={1}
          />
          {/* First and last day only. A label per day collides at any real window
              length, and the tooltip carries the rest. */}
          {[0, data.length - 1].filter((i, idx, all) => all.indexOf(i) === idx).map((i) => (
            <text
              key={i}
              x={x(i)}
              y={HEIGHT - 8}
              textAnchor={i === 0 ? 'start' : 'end'}
              fontSize={11}
              fill={theme.palette.text.secondary}
            >
              {data[i]!.day}
            </text>
          ))}
        </Box>

        {active && (
          <Box
            sx={{
              position: 'absolute',
              top: 0,
              right: 0,
              px: 1,
              py: 0.5,
              borderRadius: 1,
              bgcolor: 'background.paper',
              border: 1,
              borderColor: 'divider',
              pointerEvents: 'none',
            }}
          >
            <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
              {active.day}
            </Typography>
            <Typography variant="caption" sx={{ display: "block" }}>
              {active.views} views · {active.edits} edits
            </Typography>
          </Box>
        )}
      </Box>
    </Stack>
  )
}
