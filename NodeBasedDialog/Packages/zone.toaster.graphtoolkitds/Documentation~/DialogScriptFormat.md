# Dialog Script Format (`.ds.md`)

This document is the canonical specification for GraphToolkitDS dialogue scripts.
It describes the format currently implemented by the package parser and compiler.

`.ds.md` files are plain text source files that compile into Graph Toolkit authoring
graphs and generated `DialogNodeGraph` runtime assets.

## Status

- Implemented syntax is documented in the main sections below.
- Planned syntax must be documented only under [Planned Syntax](#planned-syntax-not-implemented).
- If this document and the parser disagree, treat the parser as the current source
  of truth until the mismatch is fixed.

## Minimal Example

```text
Alex:
> Hello.
> This creates two sentence nodes.

@effect customer.show_qr

@choice
- Stay polite -> polite
- Be rude -> rude

@section polite
Alex:
> Fine. I can check that.

@section rude
Alex:
> No.
```

## File and Compile Rules

- Script files must end with `.ds.md`.
- The Unity compiler accepts project asset paths that start with `Assets/`.
- Unity auto-compiles imported or moved `.ds.md` assets.
- Manual compile command: `Tools > Dialog System > Compile Selected Dialog Scripts`.
- The compiler reads the source, parses it, validates it, creates or replaces the
  generated authoring graph, then creates or replaces the runtime graph.

Generated assets use the source file name:

- `Name.ds.md` generates `Name.dialoggtk`.
- The authoring graph generates `Name_Runtime.asset`.
- `@pause target` also generates `Name__target.dialoggtk` and
  `Name__target_Runtime.asset` for the pause continuation.

Folder rules:

- A script directly under `Assets/Dialogues/<Folder>` generates its authoring graph
  under `AuthoringGraphs` and its runtime graph under `RuntimeGraphs`.
- A script in a nested dialogue folder generates authoring and runtime assets beside
  the source file.
- Missing output folders are created automatically.

## Parsing Model

The parser is line-oriented.

1. `CRLF` and `CR` line endings are normalized to `LF`.
2. The source is split into lines.
3. Each line is trimmed before parsing.
4. Line numbers in diagnostics are 1-based physical line numbers after line splitting.

Leading and trailing spaces are not significant for syntax. They are also removed
from speaker names, dialogue text, directive payloads, choice text, and choice
targets.

Directive names are case-sensitive. For example, `@section` is valid and
`@Section` is an unknown directive.

There is no escaping syntax. A character such as `:`, `>`, `#`, or `->` is special
only in the line position described below.

## Ignored Lines

The parser ignores a line when its trimmed form:

- is empty
- starts with `#`
- is exactly `---`
- starts with `*`
- starts with `(`

Use these for comments, separators, simple Markdown emphasis/list notes, and
parenthetical stage notes.

Important details:

- Comments are whole-line only. `> Hello # note` keeps `# note` in the dialogue text.
- `---` is ignored only when it is exactly three hyphens after trimming.
- Ignored lines inside a choice block do not end the choice block.
- Other unrecognized non-directive lines are silently skipped. This includes plain
  prose and `-` lines outside a choice block.

## Statements

### Speaker

```text
Speaker Name:
```

A speaker line is any trimmed line that ends with `:` and does not start with `>`.
The current speaker becomes the text before the final `:`, trimmed.

The current speaker applies to later dialogue lines in the current main flow or
section until another speaker line is read.

Starting a new `@section` resets the current speaker to an empty string.

A dialogue line before any speaker line is valid and compiles with an empty
speaker name.

### Dialogue Line

```text
> Dialogue text.
```

A dialogue line starts with `>`. The text after `>` is trimmed and compiled into a
sentence node using the current speaker.

Empty dialogue lines are skipped:

```text
>
```

### Section

```text
@section section_id
```

Creates or switches to a named section. Sections are used as choice targets and
pause continuation targets.

Rules:

- `section_id` is the trimmed payload after `@section`.
- Matching is exact and case-sensitive.
- Spaces are accepted because the payload is a string, but simple identifiers such
  as `after_delivery` are recommended.
- Repeating the same section id appends more statements to the existing section.
- `@section` without a payload reports `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD`.
- Sections compile only when they are referenced by a choice or pause. Unreferenced
  sections are still parsed and validated.

### Effect

```text
@effect command_payload
```

Creates an external function node named:

```text
effect:command_payload
```

GraphToolkitDS does not parse the command payload. The game integrates it at
runtime by binding an external function prefix such as `effect:`.

Rules:

- The payload is trimmed.
- `@effect` without a payload reports `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD`.
- Project-specific effect command formats belong in the consuming game's docs.

### Function

```text
@function FunctionName
```

Creates an external function node named exactly as the trimmed payload.

Rules:

- The payload is trimmed.
- `@function` without a payload reports `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD`.
- Function names can be validated by custom validators implementing
  `IDialogScriptExternalFunctionValidator`.

### Choice

```text
@choice
- First answer -> first_section
- Second answer -> second_section
```

Creates an answer node.

Rules:

- `@choice` does not need a payload.
- If extra text follows `@choice`, the current parser ignores that text. Do not use
  this for production dialogue.
- Choice options are read from the following lines.
- Ignored lines between options are skipped and do not end the choice.
- The first non-ignored line that does not start with `-` ends the choice block.
- The ending line is then parsed normally as the next statement.
- Each option is split at the last `->` marker on the line.
- Text before the last `->` becomes the answer text.
- Text after the last `->` becomes the target section id.
- Missing `->`, empty answer text, or empty target reports
  `DIALOG_SCRIPT_MALFORMED_CHOICE`.
- Each target section must exist, or validation reports
  `DIALOG_SCRIPT_MISSING_CHOICE_TARGET`.

Because the last `->` is used, answer text can contain earlier arrows:

```text
- Ask about A -> B -> ask_about_b
```

The answer text is `Ask about A -> B` and the target is `ask_about_b`.

Compiler limits:

- A choice must have at least one valid option when compiled.
- A choice must be the last compiled statement in its sequence unless a `@pause`
  stops the sequence first.
- Choice target sections are built into the graph that contains the choice.
- The current authoring graph supports up to 12 answer ports. Extra parsed options
  are not wired into the compiled graph. Do not write more than 12 options.
- Section cycles through choices are compile errors.

### Pause

```text
@pause after_delivery
```

Stops the currently compiled sequence and marks a named section as a continuation
graph.

Rules:

- The target section id is the trimmed payload.
- The target section must exist, or validation reports
  `DIALOG_SCRIPT_MISSING_PAUSE_TARGET`.
- `@pause` without a payload reports `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD`.
- One continuation graph is generated per unique pause target section.
- The main runtime graph records the first generated pause continuation target.

Statements after `@pause` in the same main flow or section are parsed, but they are
not reachable from that compiled sequence. Put post-pause content in the target
section.

## Grammar Summary

This is a readable summary of the implemented line grammar. The real parser is
line-oriented and follows the rules above.

```text
script          = line*
line            = ignored
                | section
                | effect
                | function
                | pause
                | choice
                | speaker
                | sentence
                | unknown_directive
                | silent_ignored_text

ignored         = blank
                | "#" any_text
                | "---"
                | "*" any_text
                | "(" any_text

speaker         = any_text_except_leading_gt ":"
sentence        = ">" non_empty_text
section         = "@section" whitespace non_empty_payload
effect          = "@effect" whitespace non_empty_payload
function        = "@function" whitespace non_empty_payload
pause           = "@pause" whitespace non_empty_payload
choice          = "@choice" choice_option*
choice_option   = "-" non_empty_text "->" non_empty_section_id
unknown_directive = "@" any_text
```

`whitespace` after directive names may be spaces or tabs. Directive names without a
payload are accepted as directive keywords and then reported as missing payloads.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `DIALOG_SCRIPT_UNKNOWN_DIRECTIVE` | Error | A trimmed line starts with `@` but is not a supported directive. |
| `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD` | Error | `@section`, `@effect`, `@function`, or `@pause` has no payload. |
| `DIALOG_SCRIPT_MALFORMED_CHOICE` | Error | A choice option is missing `->`, answer text, or target section. |
| `DIALOG_SCRIPT_MISSING_CHOICE_TARGET` | Error | A choice option targets a section that does not exist. |
| `DIALOG_SCRIPT_MISSING_PAUSE_TARGET` | Error | A pause targets a section that does not exist. |
| `DIALOG_SCRIPT_VALIDATOR_CREATE_FAILED` | Warning | A custom external function validator could not be constructed. |
| `DIALOG_SCRIPT_VALIDATOR_FAILED` | Warning | A custom external function validator threw an exception. |

Custom validators can return additional diagnostics.

## External Function Validation

Editor code can validate external function names by implementing:

```csharp
IDialogScriptExternalFunctionValidator
```

Validators are discovered through Unity `TypeCache`. A validator must be a
non-abstract class with a public parameterless constructor.

GraphToolkitDS calls validators for both `@effect` and `@function` statements.
Validators should return diagnostics with the script path and line number from the
provided `DialogScriptExternalFunctionValidationContext`.

## Planned Syntax (Not Implemented)

No planned `.ds.md` syntax is defined by this package at this time.

When future syntax is proposed, document it only in this section until parser,
compiler, validation, and tests exist. Every future entry must start with:

```text
Not Implemented:
```

Do not use planned syntax in production dialogue files.
