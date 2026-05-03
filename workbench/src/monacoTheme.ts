/**
 * Vais Workbench — Monaco editor dark theme.
 * VS Code Dark+ token colors on the bg-inset surface.
 * Keep hex values in sync with :root tokens in index.css.
 *
 * Usage:
 *   <Editor beforeMount={m => m.editor.defineTheme('vais-dark', vaisDarkTheme)} theme="vais-dark" />
 */

interface ThemeRule {
  token: string
  foreground?: string
  background?: string
  fontStyle?: string
}

interface ThemeData {
  base: 'vs' | 'vs-dark' | 'hc-black' | 'hc-light'
  inherit: boolean
  rules: ThemeRule[]
  colors: Record<string, string>
  encodedTokensColors?: string[]
}

// Flat hex values matching :root tokens (rgba not supported in Monaco colors).
const t = {
  bgInset:       '#0a0b0d',
  bgSurface:     '#16181b',
  border:        '#2a2d31',       // rgba(255,255,255,0.08) on bg-base
  borderStrong:  '#3d4046',       // rgba(255,255,255,0.18) on bg-base
  textPrimary:   '#e6e8eb',
  textSecondary: '#b4b8be',
  textMuted:     '#6b7178',
  accent:        '#2dd4bf',
  accentSubtle:  '#143630',       // accent @ 12% on bg-base, flattened
  success:       '#4ade80',
  warn:          '#fbbf24',
  error:         '#f87171',
}

// VS Code Dark+ token palette.
const dp = {
  keyword:     '569cd6',
  string:      'ce9178',
  number:      'b5cea8',
  comment:     '6a9955',
  type:        '4ec9b0',
  property:    '9cdcfe',
  function:    'dcdcaa',
  punctuation: '808080',
  defaultFg:   'd4d4d4',
}

export const vaisDarkTheme: ThemeData = {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: '',                    foreground: dp.defaultFg },
    { token: 'comment',             foreground: dp.comment, fontStyle: 'italic' },
    { token: 'string',              foreground: dp.string },
    { token: 'string.yaml',         foreground: dp.string },
    { token: 'string.value.yaml',   foreground: dp.string },
    { token: 'number',              foreground: dp.number },
    { token: 'constant.numeric',    foreground: dp.number },
    { token: 'constant.language',   foreground: dp.keyword },
    { token: 'keyword',             foreground: dp.keyword },
    { token: 'type',                foreground: dp.type },
    { token: 'tag',                 foreground: dp.type },
    { token: 'key',                 foreground: dp.keyword },
    { token: 'key.identifier',      foreground: dp.property },
    { token: 'identifier',          foreground: dp.property },
    { token: 'delimiter',           foreground: dp.punctuation },
    { token: 'delimiter.bracket',   foreground: dp.defaultFg },
    { token: 'string.key.json',     foreground: dp.property },
    { token: 'string.value.json',   foreground: dp.string },
    { token: 'number.json',         foreground: dp.number },
    { token: 'keyword.json',        foreground: dp.keyword },
  ],
  colors: {
    'editor.background':                   t.bgInset,
    'editor.foreground':                   t.textPrimary,
    'editorLineNumber.foreground':         '#3d4146',
    'editorLineNumber.activeForeground':   t.textSecondary,
    'editorCursor.foreground':             t.accent,
    'editor.selectionBackground':          '#1d4a44',
    'editor.selectionHighlightBackground': '#13312d',
    'editor.inactiveSelectionBackground':  '#13312d',
    'editor.findMatchBackground':          '#3a5b56',
    'editor.findMatchHighlightBackground': '#1d4a44',
    'editor.lineHighlightBackground':      '#13151820',
    'editor.lineHighlightBorder':          '#00000000',
    'editorWhitespace.foreground':         '#2a2d31',
    'editorIndentGuide.background1':       '#1c1f23',
    'editorIndentGuide.activeBackground1': '#2a2d31',
    'editorBracketMatch.background':       '#1d4a4480',
    'editorBracketMatch.border':           t.accent,
    'editorGutter.background':             t.bgInset,
    'editorGutter.modifiedBackground':     t.warn,
    'editorGutter.addedBackground':        t.success,
    'editorGutter.deletedBackground':      t.error,
    'scrollbar.shadow':                    '#00000000',
    'scrollbarSlider.background':          '#ffffff10',
    'scrollbarSlider.hoverBackground':     '#ffffff1f',
    'scrollbarSlider.activeBackground':    '#ffffff2e',
    'editorOverviewRuler.border':          '#00000000',
    'editorWidget.background':             t.bgSurface,
    'editorWidget.border':                 t.border,
    'editorSuggestWidget.background':      t.bgSurface,
    'editorSuggestWidget.border':          t.border,
    'editorSuggestWidget.foreground':      t.textPrimary,
    'editorSuggestWidget.selectedBackground': t.accentSubtle,
    'editorSuggestWidget.highlightForeground': t.accent,
    'editorHoverWidget.background':        t.bgSurface,
    'editorHoverWidget.border':            t.border,
    'editorError.foreground':              t.error,
    'editorWarning.foreground':            t.warn,
    'editorInfo.foreground':               t.accent,
  },
}

export default vaisDarkTheme
