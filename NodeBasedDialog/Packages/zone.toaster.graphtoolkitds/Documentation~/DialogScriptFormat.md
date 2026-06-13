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

@var can_tolerate:bool = true

@choice
- [if can_tolerate] Stay polite -> polite
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
- The compiler reads the source, parses it, validates it, and updates the generated
  authoring and runtime graphs only when their compiler metadata is stale.
- Generated `.dialoggtk` and `_Runtime.asset` files are stable, reviewable project
  assets. Recompiling unchanged source should not produce file diffs.
- Generated `.dialoggtk` files are owned by their `.ds.md` source. The graph
  auto-compiler skips them after script compilation, so it does not overwrite the
  runtime graph's script-source metadata with authoring-graph metadata.
- Generated nodes carry stable compiler source keys. Runtime graph compilation reuses
  matching node sub-assets instead of deleting and recreating every node.

Generated assets use the source file name:

- `Name.ds.md` generates `Name.dialoggtk`.
- The authoring graph generates `Name_Runtime.asset`.
- `@pause target` also generates `Name__target.dialoggtk` and
  `Name__target_Runtime.asset` for the pause continuation.
- If the script declares variables with `@var`, the compiler also generates or
  updates `Name_Variables.asset` and assigns it to the generated graph settings.

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

### Variable Declaration

```text
@var variable_name:type = default_value
```

Declares a dialogue variable used by conditions, text interpolation, and runtime
game integrations.

Supported types:

- `bool`: default must be `true` or `false`.
- `int`: default must be an invariant-culture integer.
- `float`: default must be an invariant-culture floating-point number.
- `string`: default is any text after `=`, optionally wrapped in single or double
  quotes. The outer quotes are removed.

Rules:

- Variable names are exact and case-sensitive.
- Variable names may contain letters, digits, `_`, and `.`. They must start with a
  letter, `_`, or `.`.
- Every condition variable must be declared with `@var` in the same source file.
- Duplicate variable declarations report `DIALOG_SCRIPT_DUPLICATE_VARIABLE`.
- Malformed declarations report `DIALOG_SCRIPT_MALFORMED_VARIABLE`.
- `@var` without a payload reports `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD`.

Examples:

```text
@var psyche:int = 100
@var can_tolerate:bool = true
@var quest.stage:int = 2
@var client_name:string = "Nikolai"
```

Runtime note: generated variables are copied per `DialogVariablesHandler`
instance, so ordinary dialogue runs do not mutate the generated
`Name_Variables.asset`. Variables marked as persistent in authored assets still
save to `PlayerPrefs`.

### Condition Expression

Conditions are structured expressions. They are not arbitrary script code.

Supported operands:

- Declared variables.
- Boolean literals: `true`, `false`.
- Number literals: `10`, `-5`, `2.5`.
- String literals wrapped in single or double quotes.

Supported operators, from highest to lowest precedence:

| Operator | Meaning |
| --- | --- |
| `!`, `not` | Boolean NOT |
| `==`, `!=`, `<`, `<=`, `>`, `>=` | Comparison |
| `&&`, `and` | Boolean AND |
| `||`, `or` | Boolean OR |

Parentheses are supported.

Type rules:

- The final expression must evaluate to `bool`.
- `&&`, `and`, `||`, and `or` require boolean operands.
- `!` and `not` require one boolean operand.
- Numeric variables and numeric literals can use all comparison operators.
- `bool` and `string` values can only use `==` and `!=`.
- Comparing different types is invalid.

Examples:

```text
psyche < 50
quest.stage == 2
can_tolerate and psyche >= 50
not can_tolerate
client_name == "Nikolai"
```

Invalid examples:

```text
psyche and true
client_name < "Z"
psyche == "low"
```

Invalid condition syntax or type usage reports `DIALOG_SCRIPT_INVALID_CONDITION`.
Using a condition without any `@var` declarations reports
`DIALOG_SCRIPT_MISSING_VARIABLES`.

### Conditional Flow

Inline conditional blocks:

```text
@if psyche < 50
Narrator:
> You feel the pressure closing in.
@else
Narrator:
> You keep yourself together.
@endif
```

Rules:

- `@if condition` starts an inline conditional block.
- `@else` is optional.
- `@endif` is required.
- Inline condition branches rejoin the next statement after `@endif`.
- Nested `@if` blocks are supported.
- `@section` cannot appear inside an inline `@if` block. Move sections outside
  the block or use section-jump syntax.
- Missing or unexpected `@else` and `@endif` lines report deterministic
  diagnostics.

Section-jump conditional:

```text
@if quest.stage == 2 -> stage_2 else fallback
```

Rules:

- The syntax is exactly `@if condition -> true_section else false_section`.
- The `else` keyword is lowercase and must be surrounded by spaces.
- Both target sections must exist.
- A section-jump condition is terminal in its sequence, like `@choice`, unless a
  `@pause` stops the sequence first.
- Missing target sections report `DIALOG_SCRIPT_MISSING_CONDITION_TARGET`.

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
- A choice option may start with a guard: `[if condition]`.
- Guarded choices are hidden at runtime when their condition evaluates to `false`.
- A malformed guard reports `DIALOG_SCRIPT_MALFORMED_CHOICE`.
- Guard conditions follow the same syntax and validation rules as `@if`
  conditions.
- Each target section must exist, or validation reports
  `DIALOG_SCRIPT_MISSING_CHOICE_TARGET`.

Because the last `->` is used, answer text can contain earlier arrows:

```text
- Ask about A -> B -> ask_about_b
```

The answer text is `Ask about A -> B` and the target is `ask_about_b`.

Guarded choice example:

```text
@choice
- [if can_tolerate] Stay quiet -> tolerate
- Snap -> snap
```

If every choice is hidden at runtime, the dialogue ends and a warning is logged.

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
                | variable
                | section
                | effect
                | function
                | pause
                | if_block
                | if_section_jump
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
variable        = "@var" whitespace variable_name ":" variable_type whitespace? "=" whitespace? default_value
section         = "@section" whitespace non_empty_payload
effect          = "@effect" whitespace non_empty_payload
function        = "@function" whitespace non_empty_payload
pause           = "@pause" whitespace non_empty_payload
if_block        = "@if" whitespace condition line* ("@else" line*)? "@endif"
if_section_jump = "@if" whitespace condition whitespace "->" whitespace section_id whitespace "else" whitespace section_id
choice          = "@choice" choice_option*
choice_option   = "-" whitespace? guarded_text "->" non_empty_section_id
guarded_text    = ("[if" whitespace condition "]")? non_empty_text
unknown_directive = "@" any_text
```

`whitespace` after directive names may be spaces or tabs. Directive names without a
payload are accepted as directive keywords and then reported as missing payloads.

Condition grammar:

```text
condition       = or_expression
or_expression   = and_expression (("||" | "or") and_expression)*
and_expression  = unary_expression (("&&" | "and") unary_expression)*
unary_expression = ("!" | "not") unary_expression | comparison
comparison      = primary (("==" | "!=" | "<" | "<=" | ">" | ">=") primary)?
primary         = variable_name | bool | number | string | "(" condition ")"
```

This grammar is intentionally small. Project-specific game commands should live in
`@effect`, not inside conditions.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `DIALOG_SCRIPT_UNKNOWN_DIRECTIVE` | Error | A trimmed line starts with `@` but is not a supported directive. |
| `DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD` | Error | `@var`, `@section`, `@effect`, `@function`, `@pause`, or `@if` has no payload. |
| `DIALOG_SCRIPT_MALFORMED_VARIABLE` | Error | A variable declaration is not `name:type = default` or has an invalid type/default value. |
| `DIALOG_SCRIPT_DUPLICATE_VARIABLE` | Error | A variable is declared more than once. |
| `DIALOG_SCRIPT_INVALID_CONDITION` | Error | A condition has invalid syntax, uses an undeclared variable, compares incompatible types, or does not evaluate to `bool`. |
| `DIALOG_SCRIPT_MISSING_VARIABLES` | Error | A script uses a condition but declares no variables. |
| `DIALOG_SCRIPT_SECTION_IN_CONDITIONAL` | Error | `@section` appears inside an inline `@if` block. |
| `DIALOG_SCRIPT_UNEXPECTED_ELSE` | Error | `@else` has no matching `@if`. |
| `DIALOG_SCRIPT_DUPLICATE_ELSE` | Error | An inline `@if` block has more than one `@else`. |
| `DIALOG_SCRIPT_UNEXPECTED_ENDIF` | Error | `@endif` has no matching `@if`. |
| `DIALOG_SCRIPT_MISSING_ENDIF` | Error | An inline `@if` block was not closed. |
| `DIALOG_SCRIPT_MISSING_CONDITION_TARGET` | Error | A section-jump condition targets a section that does not exist. |
| `DIALOG_SCRIPT_MALFORMED_CHOICE` | Error | A choice option is missing `->`, answer text, target section, or has a malformed guard. |
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
