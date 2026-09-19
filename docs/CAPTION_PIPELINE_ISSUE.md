# Caption duplication and sentence-boundary issue

Status: confirmed in the current architecture; implementation pending.

Baseline: `c03d5b4b626a15994d754fbf286f5e91d63493ec`

## User-visible problem

- One spoken sentence can appear as multiple timeline rows.
- Continuous speech is sometimes split too aggressively, while speech without
  punctuation can remain too long.
- Every incorrectly allocated sentence identity also causes another final
  translation request and another classroom record.

## Confirmed causes

1. The Windows Live Captions adapter reads one flattened accessibility `Name`
   value. It does not receive source-native row or utterance identifiers, audio
   positions, or token timestamps. Rolling-window rewrites therefore require
   heuristic text alignment.
2. `LiveCaptionSegmenter.SplitSentences` currently turns long unpunctuated text
   into finalized reading units. A visual readability boundary must not create
   a new logical sentence, provider request, or database row.
3. The local sherpa-onnx path allocates a new `SegmentId` whenever a final
   callback arrives after the active utterance has closed. Its event contract
   does not carry the recognizer generation, audio sample range, or token time
   range needed to distinguish a duplicate callback from a genuinely repeated
   utterance.
4. Local-ASR boundaries currently follow endpoint silence (1.2 seconds after
   recognized speech, or a 20-second utterance limit). There is no punctuation
   restoration or stable-token boundary stage.
5. The WPF timeline updates an existing row by `SegmentId`. Repeated visible
   rows therefore normally indicate that upstream processing allocated several
   identities, not that the ItemsControl rendered one identity several times.

## Required correction

Separate these concepts throughout the production path:

1. recognition utterance and revisions;
2. translatable/persistable sentence;
3. visual reading line or wrap.

Visual wrapping must never allocate an identity. Local recognition events must
carry an audio-backed utterance key and sample/token range. The same audio range
must be idempotent even if the recognizer repeats a final callback; non-overlap
audio ranges must preserve intentional repeated speech even when the text is
equal.

For local English recognition, evaluate sherpa-onnx token timestamps, VAD and
the small online punctuation model. Punctuation is only boundary evidence: a
candidate should be stable across revisions or confirmed by a trusted endpoint
before it enters classroom history. Partial text can continue to update the
fixed live surface without creating scrollback rows.

Windows Live Captions remains a fallback. Remove long-draft finalization from
its identity resolver and probe whether UI Automation exposes child row
structure. If the OS exposes only flattened text, keep bounded whole-window
alignment but do not treat length or visual wrapping as completion evidence.

## Regression matrix

- Ten spoken semantic sentences produce exactly ten final identities, ten final
  provider requests, ten timeline rows, and ten isolated SQLite rows.
- Repeated partial frames, punctuation changes, shortening, restoration and
  delayed old revisions update the same identity.
- A duplicate final callback for one audio range produces no additional
  identity, request or record.
- A genuinely repeated sentence in a new non-overlapping audio range remains a
  second sentence.
- Continuous speech is tested with 0.3, 0.8, 1.2 and 1.8 second pauses and a
  30-second no-punctuation section.
- Multi-speaker turn-taking, classroom switching, capture restart and an
  unfinished stop-time tail remain lossless.
- Draft revisions remain on the fixed live surface and do not move the history
  reading position.

## Non-solutions

- Do not globally de-duplicate by text.
- Do not hide duplicate database rows in the UI.
- Do not add a fixed delay to every translation.
- Do not call an LLM for basic sentence segmentation. Fix source identity and
  boundaries before comparing larger ASR models.
