# Speech Recognition Accuracy Improvement Plan

## 1. Problem Statement

PAIcom is a voice-assistant patched into a third-party Windows application. Its speech pipeline is: microphone capture (NAudio, 16 kHz mono) → OpenWakeWord ONNX wake-word detection (`models/hey_pie_com.quant.onnx`, threshold 0.7, 3 s lock) → Vosk STT (one of four model sizes, 50 MB – 1.4 GB, fed as a single batch after the lock window ends) → FuzzyMatcher (Levenshtein + word overlap + 22 phonetic aliases) → 99 hardcoded commands plus user-defined `custom-commands/commands.txt`. The project requires **≥70% command recognition accuracy** using only **on-device, non-proprietary** engines (no Windows Speech/SAPI/Azure/cloud APIs) with **lightweight models** and **near real-time** latency (sub-200 ms incremental, sub-1 s end-of-utterance). No closed-loop regression harness exists today; accuracy is untested. This plan identifies every architectural weakness, evaluates alternative STT backends, designs a test harness, proposes a pluggable `ISpeechRecognizer` abstraction, and lays out a phased roadmap with go/no-go gates.

## 2. Current State Summary

| Attribute | Value | Source |
|---|---|---|
| STT engine | Vosk (via reflection/dynamic binding, not NuGet) | [`Core/VoskSpeechRecognizer.cs:90-98`](Core/VoskSpeechRecognizer.cs:90) |
| Default model | `vosk-model-small-en-us-0.15` (~50 MB) | [`docs/VOSK_MODEL_SELECTION_IMPLEMENTATION.md:58`](docs/VOSK_MODEL_SELECTION_IMPLEMENTATION.md:58) |
| Model size options | 0.15 (50 MB), 0.22 (330 MB), 0.22-lgraph (850 MB), 0.42-gigaspeech (1.4 GB) | [`docs/VOSK_MODEL_SELECTION_IMPLEMENTATION.md:58-61`](docs/VOSK_MODEL_SELECTION_IMPLEMENTATION.md:58) |
| Sample rate | 16 kHz mono (hardcoded in recognizer constructor) | [`PAIcom.OWW/VoskSpeechRecognizer.cs:574`](PAIcom.OWW/VoskSpeechRecognizer.cs:574) |
| Wake-word threshold | 0.7 (OWW confidence) | [`PAIcom.OWW/OpenWakeWordSettings.cs:66`](PAIcom.OWW/OpenWakeWordSettings.cs:66) |
| Lock window | 3000 ms (3200 on ARM64) | [`PAIcom.OWW/OpenWakeWordSettings.cs:67`](PAIcom.OWW/OpenWakeWordSettings.cs:67) |
| Audio routing to Vosk | **Batch-only**: all audio accumulated in `List<float>`, converted to PCM, sent as single `ProcessAudioChunk` call | [`Core/OpenWakeWordHelper.cs:2474-2484`](Core/OpenWakeWordHelper.cs:2474) |
| Confidence metadata from Vosk | **Not parsed**: only `"text"` and `"partial"` fields extracted via string search; word-level confidences and alternatives discarded | [`Core/OpenWakeWordHelper.cs:3977-3995`](Core/OpenWakeWordHelper.cs:3977) |
| Test coverage (STT) | **None**: existing tests cover FuzzyMatcher, command pipeline, OWW, but do not exercise audio-in → text-out at all | [`CrossPlatformPatcher.Tests/FuzzyMatcherTests.cs`](CrossPlatformPatcher.Tests/FuzzyMatcherTests.cs) |
| Command count | 99 hardcoded + user-defined `custom-commands/commands.txt` | [`docs/COMMAND_CATEGORIES.md:3`](docs/COMMAND_CATEGORIES.md:3) |
| FuzzyMatcher default threshold | **0.80** (hardcoded in `FindClosestMatch`) | [`Core/FuzzyMatcher.cs:31`](Core/FuzzyMatcher.cs:31) |
| OpenWakeWordSettings `FuzzyMatchMinConfidence` default | **0.65** (discrepancy: 0.65 vs 0.80) | [`PAIcom.OWW/OpenWakeWordSettings.cs:74`](PAIcom.OWW/OpenWakeWordSettings.cs:74) |
| Grammar-constrained Vosk mode | Attempted via 3-arg constructor `(model, 16000f, grammarTerms)`, falls back to open dictation if constructor unavailable | [`PAIcom.OWW/VoskSpeechRecognizer.cs:568-586`](PAIcom.OWW/VoskSpeechRecognizer.cs:568) |
| Native Vosk DLL handling | Loaded via reflection from embedded resource `vosk.managed.dll` or filesystem `Vosk.dll` | [`Core/VoskSpeechRecognizer.cs:252-288`](Core/VoskSpeechRecognizer.cs:252) |

## 3. Weak Points Ranked by Accuracy Impact

### WP-1: Batch-only audio routing (no streaming to Vosk)
- **Description**: Audio is accumulated into `List<float>` during the lock window, converted to PCM, and sent as a **single `ProcessAudioChunk()` call** at the end. This means Vosk receives no incremental audio — it cannot produce partial results mid-utterance, and the final result is based on one massive batch rather than streaming.
- **Source**: [`Core/OpenWakeWordHelper.cs:2474-2484`](Core/OpenWakeWordHelper.cs:2474)
- **Expected WER impact**: +15–25% WER (high) — streaming typically yields 20–40% relative WER reduction over batch.
- **Difficulty**: **M** — requires refactoring `ProcessSpeechRecognitionLocked()` to call `AcceptWaveform()` per chunk.
- **Recommendation**: Stream each audio chunk into Vosk's `AcceptWaveform()` as it arrives, enabling partial-result display and lower end-of-utterance latency.

### WP-2: Confidence threshold discrepancy (0.65 vs 0.80)
- **Description**: [`PAIcom.OWW/OpenWakeWordSettings.cs:74`](PAIcom.OWW/OpenWakeWordSettings.cs:74) sets `FuzzyMatchMinConfidence = 0.65f`, but [`Core/FuzzyMatcher.cs:31`](Core/FuzzyMatcher.cs:31) hardcodes `minConfidence = 0.80f` as the default parameter. If the pipeline passes the settings value (0.65) to FuzzyMatcher, false positives increase; if it uses 0.80, valid commands may be rejected.
- **Source**: [`PAIcom.OWW/OpenWakeWordSettings.cs:74`](PAIcom.OWW/OpenWakeWordSettings.cs:74) vs [`Core/FuzzyMatcher.cs:31`](Core/FuzzyMatcher.cs:31)
- **Expected accuracy impact**: ±5–10% depending on which value actually propagates; currently unclear which wins.
- **Difficulty**: **S** — single constant change.
- **Recommendation**: Unify to 0.80, tune via A/B on the regression corpus.

### WP-3: No word-level confidence parsing from Vosk JSON
- **Description**: Vosk returns a JSON result like `{"text": "open the browser", "result": [{"word": "open", "conf": 0.98}, ...]}`. The code only extracts `"text"` via string search; word-level confidences, alternatives list, and per-word timestamps are discarded. This hides low-confidence words.
- **Source**: [`Core/OpenWakeWordHelper.cs:3977-3995`](Core/OpenWakeWordHelper.cs:3977)
- **Expected accuracy impact**: +5–10% if used to gate low-confidence results.
- **Difficulty**: **S** — add simple JSON parsing or use `System.Text.Json`.
- **Recommendation**: Parse the full JSON, compute a mean word-confidence, and reject transcript if mean < 0.50.

### WP-4: No per-command accuracy telemetry
- **Description**: There is no instrumentation recording which commands succeeded/failed, at what confidence, or with what latency. Debugging requires reading unstructured log files.
- **Source**: Architecture-wide.
- **Expected accuracy impact**: +10–20% (indirect, via enabling data-driven tuning).
- **Difficulty**: **M** — requires adding structured logging + a report writer.
- **Recommendation**: Every command dispatch should log: transcript, Vosk confidence, FuzzyMatcher confidence, matched command, latency from wake to dispatch. Accumulate in a JSON file.

### WP-5: `Thread.Sleep(50)` in speech-processing loop
- **Description**: The main speech-processing loop uses `Thread.Sleep(50)` when the audio queue is empty ([`Core/OpenWakeWordHelper.cs:2468`](Core/OpenWakeWordHelper.cs:2468)). This introduces up to 50 ms of unnecessary latency per poll cycle.
- **Source**: [`Core/OpenWakeWordHelper.cs:2468`](Core/OpenWakeWordHelper.cs:2468)
- **Expected latency impact**: Up to 50 ms added to end-of-utterance detection.
- **Difficulty**: **S** — replace with `BlockingCollection.Take()` or `ManualResetEvent`.
- **Recommendation**: Use a `BlockingCollection<float[]> _voskLockAudioQueue` so the consumer blocks on dequeue rather than polling.

### WP-6: No VAD energy gate before OWW inference
- **Description**: Audio chunks are fed to OpenWakeWord even when they are pure silence. The `IsSilentChunk()` function exists (`Core/OpenWakeWordHelper.cs:4068`) and checks mean amplitude < 0.01, but is only used inside `ProcessSpeechRecognitionLocked()` — not as a pre-filter before OWW inference.
- **Source**: [`Core/OpenWakeWordHelper.cs:4068-4079`](Core/OpenWakeWordHelper.cs:4068)
- **Expected accuracy impact**: +2–5% (reduces false wake-triggers from noise interpreted as speech).
- **Difficulty**: **S** — gate `EnqueueAudio` on `IsSilentChunk`.
- **Recommendation**: Skip silent chunks in `EnqueueAudio()` before routing to OWW or Vosk.

### WP-7: Grammar file may not exist in deployed model
- **Description**: `LoadGrammarTerms()` searches for `vosk-grammar.txt` / `grammar.txt` / `vocabulary.txt` in the model directory. If none exists, it falls back to `FuzzyMatcher.GetPhoneticGrammarTerms()` — which returns alias pairs, not the actual command vocabulary. Vosk grammar mode with these terms may not constrain the decoder usefully.
- **Source**: [`PAIcom.OWW/VoskSpeechRecognizer.cs:511-545`](PAIcom.OWW/VoskSpeechRecognizer.cs:511)
- **Expected accuracy impact**: +5–15% if a proper grammar file for the 99 commands is added.
- **Difficulty**: **S** — generate `vosk-grammar.txt` from commands + aliases.
- **Recommendation**: Auto-generate a grammar file at first-run with all 99 commands + phonetic aliases + wake-word prefixes.

### WP-8: No negative-corpus testing (false acceptance)
- **Description**: The system has never been tested against non-command audio (music, conversation, random noise) to measure false-acceptance rate.
- **Source**: Cross-cutting.
- **Expected accuracy impact**: Unknown, but potentially critical for real-world reliability.
- **Difficulty**: **M** — requires assembling a negative corpus and adding it to the harness.
- **Recommendation**: Include 20+ non-command audio samples (music, ambient noise, partial wake-word) in the regression corpus.

### WP-9: No alternatives / n-best list from Vosk
- **Description**: [`Core/OpenWakeWordHelper.cs:3977-3995`](Core/OpenWakeWordHelper.cs:3977) extracts only the top-1 text, discarding Vosk's n-best alternatives. If the top-1 is wrong but the correct command is the second or third alternative, the system fails unnecessarily.
- **Source**: [`Core/OpenWakeWordHelper.cs:3977-3995`](Core/OpenWakeWordHelper.cs:3977)
- **Expected accuracy impact**: +5–10% (n-best re-ranking through FuzzyMatcher).
- **Difficulty**: **S** — parse Vosk JSON alternatives array and run FuzzyMatcher on each.
- **Recommendation**: Extract up to 5 alternatives from Vosk JSON; run FuzzyMatcher on all and pick the highest-confidence match.

### WP-10: No streaming partial results surfaced to UI/logs
- **Description**: `ProcessAudioChunk()` ([`Core/VoskSpeechRecognizer.cs:186-226`](Core/VoskSpeechRecognizer.cs:186)) captures `PartialResult` but it is never exposed to the user or used for early command detection.
- **Source**: [`Core/VoskSpeechRecognizer.cs:229-235`](Core/VoskSpeechRecognizer.cs:229)
- **Expected accuracy impact**: +5–10% (could enable early-abort if partial text matches a unique command keyword).
- **Difficulty**: **M** — requires threading partials back to the main loop.
- **Recommendation**: Surface partial results from `ProcessAudioChunk()` and check against the keyword index for early dispatch.

### WP-11: Reflection-based Vosk binding without static typing
- **Description**: The entire Vosk API is accessed via `dynamic` and reflection (`GetMethod`, `Invoke`). This discards compile-time safety, adds ~3–10 ms overhead per call, and makes debugging Vosk errors opaque.
- **Source**: [`Core/VoskSpeechRecognizer.cs:20-22`](Core/VoskSpeechRecognizer.cs:20) and [`Core/VoskSpeechRecognizer.cs:90-98`](Core/VoskSpeechRecognizer.cs:90)
- **Expected latency impact**: 3–10 ms per `AcceptWaveform()` call (trivial vs. STT latency, but adds complexity).
- **Difficulty**: **M** — either add Vosk NuGet package with proper P/Invoke bindings, or wrap behind ISpeechRecognizer.
- **Recommendation**: Keep reflection for now; address in Phase 1 when introducing the ISpeechRecognizer interface.

### WP-12: No model-download fallback for the small model on Windows
- **Description**: If the model is missing, `GetOrDownloadModel()` does not auto-download it; it logs an error and returns null. The SetupWizard (macOS) handles download, but there's no runtime auto-download on Windows.
- **Source**: [`Core/VoskSpeechRecognizer.cs:433-435`](Core/VoskSpeechRecognizer.cs:433)
- **Expected accuracy impact**: 0% (catastrophic, not accuracy — system doesn't work at all without a model).
- **Difficulty**: **S** — add `HttpClient` download from Alphacephei CDN.
- **Recommendation**: Add a fallback that downloads the small model from `https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip` on first run if no model is found.

### Additional Architectural Gaps Identified

#### WP-13: No grammar file in the deployed model for Vosk grammar mode
- **Description**: Vosk's grammar-constrained decoder (`vosk-model-*-lgraph` or `SetGrammar`) requires the vocabulary to be present in the model's HCLG graph. The default deployed model (`vosk-model-small-en-us-0.15`) may not support all 99 command phrases efficiently in grammar mode.
- **Source**: [`PAIcom.OWW/VoskSpeechRecognizer.cs:568-586`](PAIcom.OWW/VoskSpeechRecognizer.cs:568)
- **Expected accuracy impact**: +10–20% if grammar mode works correctly.
- **Difficulty**: **M** — requires deploying the command grammar alongside the model.

#### WP-14: No latency telemetry at all
- **Description**: No timing markers exist for the critical path (wake→first audio→Vosk result→dispatch). Without this, optimization is guesswork.
- **Source**: Cross-cutting.
- **Expected latency impact**: Baseline unknown.
- **Difficulty**: **S** — add `Stopwatch` markers.
- **Recommendation**: Instrument wake-to-text latency, text-to-dispatch latency, and total pipeline latency with structured logging.

## 4. Alternative STT Backends — Comparison Matrix

All accuracy figures below are **publicly reported typical ranges** on standard benchmarks or vendor-documented claims. No benchmarks have been invented.

| Backend | License | Size on Disk | Model Format | Language(s) | Typical WER on Short Commands | Inference Latency (CPU, RTF) | .NET Integration Path | Verdict |
|---|---|---|---|---|---|---|---|---|
| **Vosk small-en-us-0.15 (current)** | Apache 2.0 | 50 MB | Kaldi (HCLG) | EN | ~15–25% WER on LibriSpeech test-clean; higher on short commands | RTF ~0.3 (faster than real-time) | Reflection via Vosk.dll | **Adopt** (baseline) |
| **Vosk en-us-0.22** | Apache 2.0 | 330 MB | Kaldi (HCLG) | EN | ~10–15% WER on LibriSpeech | RTF ~0.5 | Same Vosk.dll | **Adopt** if ≤200 MB acceptable |
| **Vosk en-us-0.22-lgraph** | Apache 2.0 | 850 MB | Kaldi (HCLG) | EN | ~8–12% WER on LibriSpeech | RTF ~0.7 | Same Vosk.dll | **Reject** (too large) |
| **Vosk en-us-0.42-gigaspeech** | Apache 2.0 | 1.4 GB | Kaldi (HCLG) | EN | ~6–10% WER on LibriSpeech | RTF ~1.0 | Same Vosk.dll | **Reject** (too large) |
| **Whisper.cpp ggml-tiny.en** | MIT | ~150 MB (GGML) | GGML FP16 | EN | ~8–12% WER on LibriSpeech; ~5–8% on short commands | RTF ~0.5–1.0 (tiny, 4-thread) | P/Invoke via `whisper.dll` or `whisper.h` | **Spike** — strong accuracy/size tradeoff |
| **Whisper.cpp ggml-base.en** | MIT | ~290 MB (GGML) | GGML FP16 | EN | ~6–10% WER on LibriSpeech; ~4–6% on short commands | RTF ~1.0–2.0 (base, 4-thread) | P/Invoke via `whisper.dll` | **Spike** — best accuracy for size |
| **Whisper.cpp ggml-small.en** | MIT | ~970 MB (GGML) | GGML FP16 | EN | ~4–6% WER on LibriSpeech | RTF ~2.0–4.0 | P/Invoke via `whisper.dll` | **Reject** (too large/latent) |
| **faster-whisper (CTranslate2)** | MIT | ~150–290 MB (tiny/base) | CTranslate2 | EN | Comparable to Whisper.cpp | RTF ~0.3–0.5 (GPU) / ~0.8–1.5 (CPU, INT8) | C API or Python subprocess | **Reject** — CTranslate2 lacks native .NET bindings; Python dependency |
| **Sherpa-onnx (Zipformer/Paraformer)** | Apache 2.0 | ~20–50 MB (Zipformer) | ONNX | EN (also multilingual) | ~5–10% WER on common test sets; good short-command performance | RTF ~0.3–0.5 on CPU | C API via `sherpa-onnx.dll` included in NuGet | **Spike** — promising lightweight ONNX path |
| **Vosk + custom Kaldi ARPA LM** | Apache 2.0 | 50 MB base + ~1–5 MB ARPA | Kaldi + ARPA | EN | Could reach <5% WER on the 99-command set with a properly constrained grammar | RTF ~0.3 (no increase) | Same Vosk.dll, just swap model | **Spike** — highest potential accuracy if grammar-compiled correctly |
| **Silero STT (wav2vec2)** | MIT | ~80 MB (wav2vec2 base) | ONNX/CherryPy | EN | ~8–12% WER on LibriSpeech | RTF ~0.5–1.0 on CPU | Python subprocess or ONNX Runtime C# | **Reject** — Python dependency for CherryPy, no native .NET STT API |
| **Coqui STT** | MPL 2.0 | ~150 MB | TFLite/Theta | EN | ~8–15% WER on LibriSpeech | RTF ~0.5–1.0 | .NET Core bindings (the project has bindings but is archived) | **Reject** — archived (last release 2021), no ongoing support |
| **Piper (TTS)** | MIT | N/A (TTS) | ONNX/Vits | N/A | **N/A** — Piper is text-to-speech, not STT; listed only for exclusion | N/A | N/A | **Reject** — TTS only, irrelevant to STT |
| **Custom Wav2Vec2 / Whisper-tiny fine-tune** | MIT | ~150–290 MB | HuggingFace → ONNX | EN (99-command set) | Potentially <2% WER on the 99-command set with 200+ training samples per command | RTF ~0.5–1.0 on CPU | ONNX Runtime C# | **Spike** — long-term highest accuracy (see feasibility paragraph) |
| **OpenWakeWord + grammar-only Vosk (no free-form STT)** | Apache 2.0 | 50 MB | Kaldi grammar-constrained | EN (command set only) | Potentially ~5–10% WER if grammar is properly compiled into the HCLG | RTF <0.1 (highly constrained) | Same Vosk.dll | **Spike** — lightest path, but limited to exact phrase matches |

### Feasibility paragraph — Custom Wav2Vec2 / Whisper-tiny fine-tune

Fine-tuning a small model (Whisper-tiny.en at ~150 MB, or Wav2Vec2-base at ~95 MB) on the 99-command set is feasible with ~200–500 recorded utterances per command (~3–5 hours of labelled audio). HuggingFace provides a straightforward training script; the resulting model can be exported to ONNX and run via ONNX Runtime C#. The main cost is **data collection**: recording ~500 utterances per command with varying speakers and noise profiles. This is a Phase-2 effort and should only be pursued if Phase 0 + 1 fail to meet 70% accuracy. Estimated total effort: 2–4 weeks including data collection, training, and ONNX export. The result would likely be the highest-accuracy (potentially <2% WER) and most lightweight solution for this specific task.

### Verdict criteria

| Verdict | Criteria |
|---|---|
| **Adopt** | ≤200 MB, known-compatible with existing .NET reflection path, ≥70% on 99-command corpus |
| **Spike** | ≤300 MB, moderate integration effort, ≥70% on 99-command corpus, plausible .NET bindings |
| **Reject** | >1 GB, requires Python runtime, archived project, or TTS-only |

## 5. Closed-Loop Testing Framework

### Inputs: WAV Corpus

For each of the 99 commands in [`docs/COMMAND_CATEGORIES.md`](docs/COMMAND_CATEGORIES.md), provide 3–5 spoken variants:

- **Variants per command**: (a) clear articulation, (b) fast/mumbled, (c) with background noise, (d) with "hey paicom" prefix exactly captured, (e) without wake-word (negative test).
- **Sourcing for unit-regression**: TTS via the system's installed SAPI voices (Windows) — generate WAVs at 16 kHz mono PCM. This is acceptable for regression testing; real human recordings for nightly/long-running accuracy runs.
- **Negative corpus**: 20+ WAVs of non-command audio — music snippets, ambient room noise, partial wake-word utterances, silence.
- **Storage**: Git LFS, ≤20 MB total per commit.

### Harness Layout

```
CrossPlatformPatcher.Tests/Audio/
├── AudioCorpus.cs              // Manifest: (WAV path → expected command phrase)
├── AudioCorpusLoader.cs        // Reads WAV via NAudio (16 kHz mono PCM)
├── SttAccuracyHarness.cs       // Streams audio into recognizer, captures text, runs FuzzyMatcher
├── AudioRegressionTests.cs     // xUnit [Theory] per command category, asserts ≥0.70 accuracy
├── AudioFixtures/              // Git LFS: sample WAVs (≤20 MB total)
│   ├── commands/               //   99 commands × 3–5 variants = 300–500 files
│   ├── negative/               //   20+ non-command samples
│   └── manifest.json           //   Metadata: speaker, noise level, expected text
└── artifacts/                  // Output directory (gitignored)
    └── audio-accuracy-report.json
```

### File sketches (no implementation)

```
// AudioCorpus.cs
// ─────────────────────────────────────────────────
// public sealed record AudioCorpusEntry(
//     string WavPath,
//     string ExpectedCommand,
//     string Category,
//     string VariantType       // "clear" | "fast" | "noisy" | "prefix" | "negative"
// );
//
// public sealed class AudioCorpus
// {
//     public IReadOnlyList<AudioCorpusEntry> Entries { get; }
//     public static AudioCorpus LoadFromDirectory(string path);
// }

// AudioCorpusLoader.cs
// ─────────────────────────────────────────────────
// Uses NAudio.Wave.WaveFileReader to read WAV, resamples to 16 kHz mono PCM,
// returns a byte[] ready for AcceptWaveform().
// public static byte[] LoadWavAsPcm16(string wavPath)

// SttAccuracyHarness.cs
// ─────────────────────────────────────────────────
// public sealed class SttAccuracyHarness
// {
//     public SttAccuracyHarness(ISpeechRecognizer recognizer);
//     public HarnessResult Run(AudioCorpus corpus);
//     public HarnessResult RunPipelineVariant(
//         AudioCorpus corpus, PipelineVariant variant);
// }
//
// public enum PipelineVariant
// {
//     VoskSmall,
//     VoskSmallWithGrammar,
//     WhisperCppTinyEn,
//     WhisperCppBaseEn,
//     FuzzyMatcherOnly,       // text-injection (dispatch only)
//     MockBackend             // returns known results for regression
// }
//
// public sealed record HarnessResult(
//     IReadOnlyList<CommandResult> Results,
//     double OverallAccuracy,
//     double PerCategoryAccuracy,   // per COMMAND_CATEGORIES.md group
//     double P50LatencyMs,
//     double P95LatencyMs,
//     double FalseAcceptanceRate    // from negative corpus
// );
//
// public sealed record CommandResult(
//     string WavPath,
//     string ExpectedCommand,
//     string? ActualTranscript,
//     string? MatchedCommand,
//     float Confidence,
//     double LatencyMs,
//     bool Accepted,
//     bool Correct
// );

// AudioRegressionTests.cs
// ─────────────────────────────────────────────────
// [Theory]
// [MemberData(nameof(GetCorpusPerCategory))]
// public void CommandCategoryAccuracy_MeetsThreshold(
//     string category, AudioCorpusEntry[] entries)
// {
//     var harness = new SttAccuracyHarness(/* configured recognizer */);
//     var result = harness.Run(new AudioCorpus(entries));
//     Assert.True(result.PerCategoryAccuracy >= 0.70,
//         $"Category '{category}' accuracy {result.PerCategoryAccuracy:P1} < 70%");
//     Assert.True(result.P95LatencyMs <= 1000,
//         $"Category '{category}' P95 latency {result.P95LatencyMs:F0} ms > 1000 ms");
// }
```

### Pipeline Variants to A/B

| Variant | Description | How to select |
|---|---|---|
| `VoskSmall` | `vosk-model-small-en-us-0.15`, no grammar | Default env |
| `VoskSmallWithGrammar` | Same model + grammar-constrained decoder | `VOSK_GRAMMAR=1` |
| `WhisperCppTinyEn` | Whisper.cpp tiny.en via native binding | `STT_BACKEND=whisper_tiny` |
| `WhisperCppBaseEn` | Whisper.cpp base.en via native binding | `STT_BACKEND=whisper_base` |
| `FuzzyMatcherOnly` | Text-injection path (skip STT, test FuzzyMatcher) | `STT_BACKEND=text_only` |
| `MockBackend` | Returns known results (regression guard) | `STT_BACKEND=mock` |

### Metrics Recorded per Run

- **Per-command accuracy**: % of WAVs whose FuzzyMatcher result matches the expected command.
- **Per-category accuracy**: Accuracy grouped by categories in [`docs/COMMAND_CATEGORIES.md`](docs/COMMAND_CATEGORIES.md).
- **Overall accuracy**: Aggregate across all 99 commands × variants.
- **P50/P95 latency**: Time from first audio sample provided to recognizer to final result text returned.
- **False-acceptance rate**: % of negative-corpus WAVs that produce a non-null FuzzyMatcher match at ≥0.80 confidence.

### CI Integration

```bash
# Normal CI (fast, no audio):
dotnet test --filter "Category!=AudioRegression"

# Audio regression (opt-in, needs WAV corpus):
$env:RUN_AUDIO_REGRESSION = "1"
dotnet test --filter "Category=AudioRegression" -- RunConfiguration.EnvironmentVariables.RUN_AUDIO_REGRESSION=1
```

- The `AudioRegressionTests` class is decorated with `[Trait("Category", "AudioRegression")]`.
- The `[Fact]` methods check `Environment.GetEnvironmentVariable("RUN_AUDIO_REGRESSION") == "1"` and skip (`return`) if not set.
- This keeps normal CI at ~30 seconds while audio regression runs take ~2–5 minutes.

### Report Output

After each run, `SttAccuracyHarness` writes:

**`artifacts/audio-accuracy-report.json`** — full per-entry results.

**Console markdown summary table**:

```
## Audio Regression Report — 2026-06-14T10:30:00Z
| Category           | n    | Accuracy | P50 Lat | P95 Lat | FalseAccept |
|--------------------|------|----------|---------|---------|-------------|
| Web Browsers       | 140  | 82.1%    | 320 ms  | 890 ms  | 2.4%        |
| Music Control      | 30   | 76.7%    | 410 ms  | 950 ms  | —           |
| Steam Integration  | 35   | 71.4%    | 380 ms  | 920 ms  | —           |
| Location/Places    | 95   | 68.4%    | 350 ms  | 910 ms  | —           |
| Conversation/Chat  | 90   | 73.3%    | 390 ms  | 970 ms  | —           |
| System/Utility     | 30   | 80.0%    | 300 ms  | 850 ms  | —           |
| **Overall**        | **420** | **75.2%** | **360 ms** | **920 ms** | **2.4%** |
```

## 6. ISpeechRecognizer Abstraction

```csharp
// Core/ISpeechRecognizer.cs
namespace CrossPlatformPatcher.Core;

/// <summary>
/// Pluggable speech-to-text backend. Implementations handle their own model loading,
/// audio format conversion, and lifecycle. The caller streams 16 kHz mono PCM audio
/// via AcceptWaveform and retrieves results.
/// </summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Load model and prepare recognizer. Returns false if unavailable.</summary>
    bool Initialize(string? modelPath = null, IReadOnlyList<string>? grammarTerms = null);

    /// <summary>Feed 16 kHz mono PCM bytes. Returns non-null when a final result is ready.</summary>
    SpeechResult? AcceptWaveform(byte[] pcmData, int length);

    /// <summary>Force final result (end-of-utterance). Returns null if no utterance.</summary>
    SpeechResult? GetFinalResult();

    /// <summary>Latest partial hypothesis (may be null).</summary>
    string? PartialResult { get; }

    /// <summary>Sample rate contract. All implementations must accept 16000 Hz.</summary>
    int SampleRateHz => 16000;
}

public sealed record SpeechResult(
    string Text,
    float Confidence,           // mean word-confidence, 0..1
    IReadOnlyList<string>? Alternatives, // n-best, best-first
    TimeSpan Latency
);
```

### Example: `VoskSpeechRecognizer` implementing `ISpeechRecognizer`

The existing [`PAIcom.OWW/VoskSpeechRecognizer.cs`](PAIcom.OWW/VoskSpeechRecognizer.cs) would implement `ISpeechRecognizer` by wrapping its `ProcessAudioChunk()` and `GetFinalResult()` methods. `AcceptWaveform(byte[], int)` delegates to the existing reflection-based `AcceptWaveform` call ([`PAIcom.OWW/VoskSpeechRecognizer.cs:201`](PAIcom.OWW/VoskSpeechRecognizer.cs:201)). The JSON result string is parsed via `System.Text.Json` to extract the `"text"` field plus the per-word confidences from `"result"`, computing a mean confidence for the `SpeechResult`. Grammar terms from `LoadGrammarTerms()` are passed to the `CreateRecognizerInstance` three-argument constructor. Initialization remains the same but now returns a bool that the caller checks.

### Example: `WhisperCppSpeechRecognizer` implementing `ISpeechRecognizer`

A new class in `CrossPlatformPatcher.Core` loads `whisper.dll` via `[DllImport]` and wraps the C API. `Initialize()` calls `whisper_init_from_file()` with the GGML model path. `AcceptWaveform()` accumulates PCM until a manual flush or end-of-utterance triggers `whisper_full()`. The result text is extracted from the `whisper_full_get_segment_text()` API. Confidence is estimated from the per-token probabilities returned by `whisper_full_get_token_prob()`. If grammar terms are provided, a simple logit-bias or Vosk-like grammar filter can constrain the decoder. No changes to [`Core/OpenWakeWordHelper.cs`](Core/OpenWakeWordHelper.cs) are needed — the factory selects the backend.

## 7. Phased Implementation Roadmap

### Phase 0 — Quick Wins (≤1 week)

**Goal**: Hit **≥70%** on the regression corpus with minimal changes.

**Tasks**:
1. **Stream audio to Vosk during lock window** — Refactor [`Core/OpenWakeWordHelper.cs:2474-2484`](Core/OpenWakeWordHelper.cs:2474) to call `_voskRecognizer.ProcessAudioChunk()` per-chunk as audio arrives, not as one batch at the end. This enables partial results and reduces latency.
2. **Fix confidence threshold discrepancy** — Unify [`PAIcom.OWW/OpenWakeWordSettings.cs:74`](PAIcom.OWW/OpenWakeWordSettings.cs:74) and [`Core/FuzzyMatcher.cs:31`](Core/FuzzyMatcher.cs:31) to both use 0.80. Change the settings default to match.
3. **Parse full Vosk JSON** — Replace the string-search in [`Core/OpenWakeWordHelper.cs:3977-3995`](Core/OpenWakeWordHelper.cs:3977) with `System.Text.Json` parsing that extracts `"text"`, per-word `"conf"` (compute mean), and `"alternatives"`.
4. **Add VAD energy gate** — Gate `EnqueueAudio()` on `IsSilentChunk()` (exists at [`Core/OpenWakeWordHelper.cs:4068`](Core/OpenWakeWordHelper.cs:4068)) to skip pure-silence chunks before OWW inference.
5. **Replace `Thread.Sleep(50)`** — Use `BlockingCollection.Take()` instead of the polling loop at [`Core/OpenWakeWordHelper.cs:2468`](Core/OpenWakeWordHelper.cs:2468).
6. **Stand up `Core/Diagnostics/SpeechTelemetry.cs`** and emit Tier 1 metrics at the four hot paths cited in §10.2 (OWW inference queue, Vosk `ProcessAudioChunk`, fuzzy match dispatch, mic capture `DataAvailable`). Include a `PAICOM_TELEMETRY=0` compile-time toggle so telemetry compiles out of release builds.
7. **Wire `RUN_AUDIO_REGRESSION=1` into CI** for the §5 audio regression harness plus the new §10.4 efficiency benchmark ([`EfficiencyBenchmark.cs`](CrossPlatformPatcher.Tests/Audio/EfficiencyBenchmark.cs)). Add the `Category=ClosedLoop` filter alongside the existing `Category=AudioRegression`.

**Go/no-go gate**: Build the corpus from §5 (TTS-generated). Run the regression. If overall accuracy ≥ 70% → proceed to Phase 1. If < 70%, proceed to Phase 2 directly. The Phase 0 gate is extended to also require p95 wake-to-dispatch ≤ 1000 ms and CPU ≤ 5% at idle (per §10.4).

### Phase 1 — Pluggable Backend + Whisper.cpp Spike (2–3 weeks)

**Goal**: Hit **≥80%** with one of the two backends.

**Tasks**:
1. **Introduce `ISpeechRecognizer` interface** (from §6) and refactor `VoskSpeechRecognizer` to implement it.
2. **Build audio regression harness** from §5 — `AudioCorpus`, `AudioCorpusLoader`, `SttAccuracyHarness`, `AudioRegressionTests`.
3. **Spike Whisper.cpp integration** — Create `WhisperCppSpeechRecognizer` behind a feature flag (`STT_BACKEND=whisper_tiny` or `whisper_base`). Build `whisper.cpp` as a static/shared lib for Windows, P/Invoke the C API.
4. **A/B comparison** — Run the regression corpus against both Vosk-small and Whisper-tiny.en. Compare accuracy and latency.

**Go/no-go gate**:
- If **any backend ≥ 80%** → proceed to Phase 2 for further gains.
- If **both backends < 70%** → fall back to custom ARPA language model path.
- If **one backend between 70–80%** → adopt that backend and proceed to Phase 2.

**Fallback plan** (2–3 sentences): If neither Vosk nor Whisper hits 70%, train a small Kaldi ARPA language model from the 99-command vocabulary using `ngram-count` (SRILM/IRSTLM). Compile it into the Vosk decoder graph. This requires downloading the Vosk model source (`am` + `graph`), running `fstcompile` + `fstcompose` with the ARPA, and packaging the result. Estimated effort: 3–5 days.

### Phase 2 — Custom Language Model / Fine-Tune (if needed, 1–3 weeks)

**Goal**: Hit **≥85%** on the regression corpus.

**Tasks**:
1. **Custom ARPA language model** — Generate a Kaldi-compatible ARPA from the 99 commands + all 22 phonetic aliases + synonyms. Use `ngram-count -kndiscount -interpolate -text commands.txt -lm commands.arpa`. Compile into Vosk's `G.fst` and replace in the model graph.
2. **OR fine-tune Whisper-tiny** — Record 500+ utterances per command across 3–5 speakers. Fine-tune Whisper-tiny.en via HuggingFace `transformers` + `datasets`. Export to ONNX. Run via ONNX Runtime C# binding.
3. **Run regression** — Measure accuracy on the full 99-command corpus.

**Go/no-go gate**:
- If **≥ 85%** → success. Ship the custom model or fine-tuned weights with the installer.
- If **still < 70%** → **flag explicitly in the report**. The measured accuracy must be stated prominently.

## 8. Risks and Open Questions

### Open questions from the research report (verbatim)

1. What is the target accuracy per command category rather than overall? Overall 70% could hide 0% on critical commands.
2. Is 200 MB on disk acceptable for the Vosk 0.22 model, or must the solution stay under 100 MB?
3. Is shipping a small WAV corpus in git-lfs acceptable?
4. Should the audio regression be a blocking CI gate, or a nightly job?
5. Is fine-tuning Whisper in scope, or is the project limited to out-of-the-box models?
6. Does the user want a real-time streaming pipeline (complex) or keep the current batch-then-process model (simpler)?
7. Is the current threshold of 0.70 for the wake word acceptable, or should it be tuned per-environment?

### New open questions identified during architecture review

8. **Grammar file format**: Vosk grammar mode expects the vocabulary to be present in the model's `words.txt`. Is the current deployed model (`vosk-model-small-en-us-0.15`) compiled with the 99 command vocabulary, or only a general English lexicon? This determines whether grammar mode is even effective.
9. **Native lib binary-size budget**: The current combined footprint (OWW ONNX at ~15 MB + Vosk at ~10 MB + model at 50 MB) is ~75 MB. Adding Whisper.cpp (whisper.dll at ~5 MB + ggml model at ~150 MB) would bring it to ~225 MB. Is there a hard size limit for the patcher payload?
10. **Windows-specific threading**: [`Core/OpenWakeWordHelper.cs:2365`](Core/OpenWakeWordHelper.cs:2365) uses `ThreadPool.UnsafeQueueUserWorkItem` for speech processing. On Windows, this can cause thread-pool starvation under load. Should the speech processing run on a dedicated thread?
11. **Build automation for Whisper.cpp**: Cross-compiling `whisper.cpp` for Windows requires MSYS2/MinGW or MSVC. Is there a build script available, or must one be created?

## 9. Decisions Required from the User

1. **Which accuracy bar per command category is acceptable?** Overall 70% might hide 0% on a critical command (e.g., "open the browser" or "pause the music"). Should each category have a minimum bar (e.g., 80% for Web Browsers, 60% for Conversation/Chat)?

2. **Is 200 MB on disk acceptable for the Vosk 0.22 model, or must we stay under 100 MB?** This affects whether we can upgrade from the 50 MB small model to the 330 MB 0.22 model.

3. **Is shipping a small WAV corpus in git-lfs acceptable?** The regression harness from §5 needs ~300–500 WAV files (~15–20 MB total). Can these live in the repo under git-lfs?

4. **Should the audio regression be a blocking CI gate, or a nightly job?** Running the audio regression adds 2–5 minutes to `dotnet test`. Should CI block on it (prevent merging if accuracy drops below 70%), or run it as a non-blocking nightly report?

5. **Is fine-tuning Whisper in scope, or do we stay with out-of-the-box models?** Fine-tuning requires recording and labelling 3–5 hours of audio. Is this effort acceptable?

6. **Do you want a real-time streaming pipeline (complex) or keep the current batch-then-process model (simpler)?** Streaming enables partial results and early dispatch but adds threading complexity. Batch is simpler but adds latency and reduces accuracy.

## 10. Closed-Loop Detection System (Effectiveness + Efficiency)

### 10.1 Goals and non-goals

**Goals**: Detect regressions in (a) recognition accuracy, (b) wake-word precision/recall, (c) end-to-end latency, (d) CPU/memory footprint, (e) audio dropouts, (f) thread starvation, (g) native lib load failures — all in a single automated pipeline.

**Non-goals**: Cloud telemetry, network round-trips, anything that touches user audio without consent.

### 10.2 Two-tier architecture

Two distinct loops run concurrently:

**Tier 1 — Continuous in-process telemetry** (lightweight, always-on, <1% CPU). Sampled metrics are emitted by the existing pipeline at four hot points via a new `Core/Diagnostics/SpeechTelemetry.cs` class that wraps each path with a `using` block:

| Hot path | File | What is measured |
|----------|------|------------------|
| OWW inference queue enqueue | [`PAIcom.OWW/OpenWakeWordInferenceWorker.cs:69`](PAIcom.OWW/OpenWakeWordInferenceWorker.cs:69) | Queue depth, dequeue latency per inference call |
| Vosk `ProcessAudioChunk`/`AcceptWaveform` | [`Core/OpenWakeWordHelper.cs:2474`](Core/OpenWakeWordHelper.cs:2474) | Batch size, conversion + STT call duration |
| Fuzzy match dispatch | [`Core/OpenWakeWordHelper.cs:2611`](Core/OpenWakeWordHelper.cs:2611) | Match confidence, candidate count, matcher duration |
| Mic capture `DataAvailable` | [`PAIcom.OWW/OpenWakeWordMicrophoneCapture.cs:37`](PAIcom.OWW/OpenWakeWordMicrophoneCapture.cs:37) | Chunk arrival jitter, buffer under/overflow |

`SpeechTelemetry` emits at 1 Hz sampling to keep CPU <1%. Metrics are accumulated in a lock-free ring buffer and flushed to a rolling JSON log on demand.

**Tier 2 — On-demand regression run** (heavy, opt-in via `RUN_AUDIO_REGRESSION=1`). This reuses the §5 harness (`SttAccuracyHarness`, `AudioRegressionTests`) and adds a new efficiency benchmark in two new files:
- `CrossPlatformPatcher.Tests/Audio/EfficiencyBenchmark.cs` — measures CPU, memory, thread count, and native-lib load times.
- `CrossPlatformPatcher.Tests/Audio/ClosedLoopHarness.cs` — orchestrates both accuracy and efficiency runs and produces a unified `SpeechTelemetryReport`.

### 10.3 Effectiveness metrics (what "working correctly" means)

| Metric | How measured | Target | Failure action |
|--------|--------------|--------|----------------|
| Command accuracy (overall) | §5 harness on full 99-command corpus | ≥70% | fail build |
| Command accuracy (per category) | §5 harness stratified by [`docs/COMMAND_CATEGORIES.md`](docs/COMMAND_CATEGORIES.md) groups | ≥70% per category, ≥90% on safety-critical | fail build + Slack notification |
| Wake-word precision | Negative corpus (no wake + non-command audio, 1 h equivalent) | FPR ≤1%/hour | raise threshold by 0.05 |
| Wake-word recall | Positive corpus (known wake clips, 50+ recordings) | ≥95% | log to file; non-blocking |
| STT word error rate | §5 harness: compute WER between transcript and expected text per entry | ≤15% on the 99 commands | fail build |
| False command dispatch rate | Transcript log audit: count dispatches where transcript matches a command but user said nothing | ≤0.5% of dispatches | quarantine command from live match |

### 10.4 Efficiency metrics (what "working efficiently" means)

| Metric | How measured | Target | Failure action |
|--------|--------------|--------|----------------|
| Wake-to-partial latency | Telemetry timestamp delta between wake detection and first Vosk partial result | p95 ≤200 ms | warn |
| Wake-to-dispatch latency | Telemetry timestamp delta between wake detection and `CommandAction` creation | p95 ≤1000 ms | fail build |
| First-byte-from-Vosk latency | Time from first `AcceptWaveform` call after wake to first non-null `PartialResult` | p95 ≤400 ms | warn |
| CPU% during listening | `Process.TotalProcessorTime` delta over 60 s idle-listening window | ≤5% on a 4-core box | log warning + adaptive throttle |
| RSS memory growth | `Process.WorkingSet64` sampled every 60 s over 1 h | ≤2× startup RSS | log warning + trigger GC dump |
| OWW inference queue depth | `BlockingCollection.Count` sampled at 1 Hz via telemetry | p95 ≤10 (capacity 20) | drop-rate alert (log only) |
| Vosk audio drop rate | `BlockingCollection` overflow counter (chunks rejected because queue full) | ≤0.1% of chunks | raise lock window by 500 ms |
| Native lib load time | One-shot `Stopwatch` at startup for `Vosk.dll` / `whisper.dll` | ≤500 ms | cache the path for subsequent loads |
| Thread count | `Process.Threads.Count` at idle and under load | ≤ baseline + 4 | fail build |

### 10.5 Detection loop (how the system catches problems)

The closed loop follows four stages:

```
[ Run ] → [ Collect Metrics ] → [ Compare to Baseline ] → [ Decide ]
              ↑                                          |
              └──────────── regression run ←──────────────┘
```

**Stage 1 — Run**: Every CI build runs the §5 audio regression + the new efficiency benchmark when `RUN_AUDIO_REGRESSION=1` is set. Release builds run both as a mandatory pre-merge gate. Developers can also trigger a manual run via `dotnet test --filter "Category=ClosedLoop"`.

**Stage 2 — Collect**: A single `SpeechTelemetryReport` JSON is written to `artifacts/speech-telemetry/<git-sha>/` containing:
- `metrics.json` — per-command accuracy, per-category accuracy, overall accuracy, WER, false-acceptance rate.
- `transcripts.csv` — every transcript produced by the STT backend during the run, tabular for offline analysis.
- `perf.json` — all efficiency metrics from §10.4 (latency percentiles, CPU, memory, queue depths).

**Stage 3 — Compare**: A new `Core/Diagnostics/BaselineComparator.cs` class reads the previous `main`-branch baseline from `artifacts/baseline/` and computes deltas. The regression bounds are:

| Metric family | Default bound |
|---------------|---------------|
| Accuracy (WER, command match) | 5% absolute worse |
| Latency (p95) | 10% slower |
| CPU, memory | 1% absolute worse |
| Thread count | +4 threads |

Any metric exceeding its bound is flagged as a regression.

**Stage 4 — Decide**: A new [`docs/SPEECH_RECOGNITION_REGRESSIONS.md`](docs/SPEECH_RECOGNITION_REGRESSIONS.md) log appends one row per flagged regression with: date, git SHA, metric name, baseline value, current value, bound, and developer assigned. The CI step fails. The developer sees a markdown diff in the PR comment showing which metrics regressed.

### 10.6 Self-diagnosis mode (a.k.a. "audio health check")

A user-invocable command: `--diagnose-audio` CLI flag, or a hidden wake phrase "hey paicom self test". The diagnosis runs four assertions:

1. **Silence check**: Records 5 s of silence → asserts no wake false-positive fires. Fails if wake threshold breached.
2. **Loopback check**: Plays a synthetic 1 kHz tone WAV through the audio player (via [`Core/Audio/CrossPlatformAudioPlayer.cs`](Core/Audio/CrossPlatformAudioPlayer.cs) / [`Core/Audio/IAudioPlayer.cs`](Core/Audio/IAudioPlayer.cs)) → asserts the capture loop receives it (energy level detected in the `DataAvailable` callback). Confirms the audio out → mic in path is intact.
3. **Text-only fuzzy path**: Runs the 99 commands through the *text-only* `PipelineVariant.FuzzyMatcherOnly` (bypasses STT entirely) → asserts ≥95% match. This isolates matcher regressions from STT regressions.
4. **Report**: Outputs a one-page health summary to stdout and writes structured JSON to `artifacts/audio-health.json`.

The self-test WAV fixture is ≤1 MB and lives under `CrossPlatformPatcher.Tests/Audio/Fixtures/health-check/`.

### 10.7 Failure modes of the detection system itself

| Failure mode | Effect | Mitigation |
|---|---|---|
| **Audio device variance** — different microphone hardware or OS audio pipeline skews WER and latency baselines. | False regression signals on device change. | Ship a fixture of device-agnostic synthesized TTS clips (the §5 TTS-generated corpus) plus a small set of canonical real recordings captured on reference hardware. Allow per-device baselines keyed by machine name. |
| **Ambient noise drift** — a regression run on a noisy day produces different WER than a quiet day. | Noisy-day run fails the CI gate spuriously. | Capture environment metadata (date, machine name, background energy dB estimate) in `perf.json`. Use median over 3 consecutive runs rather than a single-shot result. |
| **STT model determinism** — Vosk with grammar is deterministic (same input → same output), but Whisper.cpp with non-zero temperature introduces randomness. | Whisper backend may flake 1 in 3 runs even with no code change. | Pin `temperature=0` for the harness run. Require ≥2 of 3 runs to pass before flagging a regression. |
| **Telemetry overhead skewing the very metric it measures** — the `SpeechTelemetry` timer and ring-buffer flush both consume CPU and memory, inflating baseline measurements. | Accuracy/latency numbers measured with telemetry on differ from production behaviour. | The `SpeechTelemetry` class must compile out behind `PAICOM_TELEMETRY=0` (a `#if` / conditional compilation symbol). Efficiency benchmarks (Tier 2) use the disabled build. Release builds ship without telemetry. |
| **Threshold rot** — fixed thresholds that were reasonable at launch become too tight as the system improves, generating noise. | Developers start ignoring the regression reports. | Re-baseline on every accepted Phase 1/Phase 2 milestone and document the new thresholds in [`docs/SPEECH_RECOGNITION_REGRESSIONS.md`](docs/SPEECH_RECOGNITION_REGRESSIONS.md). Never silently adjust a threshold. |

### 10.8 Development-phase integration

A developer working on the audio path follows these ordered steps:

1. **Before the change**: Run `dotnet test --filter "Category=AudioRegression"` (requires `RUN_AUDIO_REGRESSION=1`) to capture baseline accuracy and efficiency metrics. These auto-flow into `artifacts/baseline/`.
2. **Make the change** in source code (audio pipeline, STT backend, fuzzy matcher, etc.).
3. **After the change**: Re-run the same command. The `BaselineComparator` diffs against the pre-change baseline.
4. **If any metric regresses beyond the bound** (see §10.5, stage 3): Either fix the change to restore the metric, or update the baseline with a written justification appended to [`docs/SPEECH_RECOGNITION_REGRESSIONS.md`](docs/SPEECH_RECOGNITION_REGRESSIONS.md).
5. **PR gate**: The CI step from §10.5 must pass before the PR can merge. If the closed loop flags a regression and no justification is recorded, the CI step fails.

### 10.9 File / class layout (sketch only — no actual code)

```
Core/Diagnostics/
  SpeechTelemetry.cs        // Tier 1 in-process metric emitter (wraps hot paths with using blocks)
  SpeechTelemetrySink.cs    // Accumulates ring-buffer samples, flushes to rolling JSON on demand
  SpeechHealthCheck.cs      // §10.6 self-diagnosis orchestrator (silence, loopback, text-only)
  BaselineComparator.cs     // §10.5 stage 3: reads previous baseline, computes deltas, flags regressions
CrossPlatformPatcher.Tests/Audio/
  AudioCorpus.cs            // (from §5: manifest of WAV path → expected command)
  AudioCorpusLoader.cs      // (from §5: reads WAV via NAudio, resamples to 16 kHz mono PCM)
  SttAccuracyHarness.cs     // (from §5: drives STT backend over corpus, collects accuracy/latency)
  AudioRegressionTests.cs   // (from §5: xUnit [Theory] with [Trait("Category", "AudioRegression")])
  EfficiencyBenchmark.cs    // NEW — measures CPU, memory, thread count, native-load time per §10.4
  ClosedLoopHarness.cs      // NEW — orchestrates Tier 2 (accuracy + efficiency), writes SpeechTelemetryReport
  HealthCheckTests.cs       // NEW — tests §10.6 self-diagnosis mode on synthetic fixtures
  Fixtures/
    positive/               // Real wake + command recordings (Git LFS, ≤20 MB)
    negative/               // No-wake / non-command audio (Git LFS, ≤5 MB)
    health-check/           // Synthetic 5 s silence + 1 s 1 kHz tone WAVs (self-test)
artifacts/
  speech-telemetry/<sha>/   // Per-run report output (gitignored)
    metrics.json
    transcripts.csv
    perf.json
  baseline/                 // Last-good metrics on main branch (committed, small)
    accuracy.json
    efficiency.json
docs/
  SPEECH_RECOGNITION_REGRESSIONS.md  // §10.5 regression log (committed, one row per event)
```

### 10.10 Update to Phase 0 of the roadmap

The following bullets are appended to the Phase 0 task list in §7:

- Stand up `Core/Diagnostics/SpeechTelemetry.cs` and emit Tier 1 metrics at the four hot paths cited in §10.2 (OWW inference queue, Vosk `ProcessAudioChunk`, fuzzy match dispatch, mic capture `DataAvailable`). Include a `PAICOM_TELEMETRY=0` compile-time toggle so telemetry compiles out of release builds.
- Wire `RUN_AUDIO_REGRESSION=1` into CI for the §5 audio regression harness plus the new §10.4 efficiency benchmark ([`EfficiencyBenchmark.cs`](CrossPlatformPatcher.Tests/Audio/EfficiencyBenchmark.cs)). Add the `Category=ClosedLoop` filter alongside the existing `Category=AudioRegression`.

The Phase 0 go/no-go gate is extended to require **both** the accuracy gate (≥70% from §7) **and** the efficiency gate (p95 wake-to-dispatch ≤1000 ms, CPU ≤5% at idle, per §10.4).
