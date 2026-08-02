// Shared Typst helpers for the ObjectRT documentation set.
//
// NOTE: recreated 2026-08-01 — this file was missing from the repository
// while ObjectRT.typ (and others) still imported it. Only the `instruction`
// helper is actually used anywhere (by ObjectRT.typ, ~50 call sites).

/// Render one entry of the instruction-set reference.
///
/// Usage:
/// ```typ
/// #instruction(
///   "ldc.i4",   // mnemonic
///   "-> i32",   // stack effect
///   "None",     // errors raised ("None" hides the line)
///   "Push a 32-bit integer constant onto the stack. Operand: bare integer."
/// )
/// ```
#let instruction(name, effect, errs, desc) = block(
  width: 100%,
  breakable: false,
  inset: (y: 6pt),
  [
    #grid(
      columns: (auto, 1fr),
      column-gutter: 16pt,
      align: (left + horizon, left + horizon),
      [
        #text(weight: "bold", size: 10.5pt)[#name]
        #if effect != "---" [
          #v(3pt)
          #text(style: "italic", size: 9pt)[#effect]
        ]
      ],
      [
        #desc
        #if errs != "None" [
          #v(3pt)
          #text(size: 8.5pt, fill: rgb("#7a3030"))[*Errors:* #errs]
        ]
      ],
    )
    #v(3pt)
    #line(length: 100%)
  ],
)
