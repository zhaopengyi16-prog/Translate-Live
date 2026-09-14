# Third-party notices

Translate Live retains its upstream Apache License 2.0 terms. The optional local-ASR experiment also uses the following components:

- **NAudio.Wasapi / NAudio.Core 3.1.0** — MIT License — <https://github.com/naudio/NAudio>
- **sherpa-onnx C# runtime 1.13.8** — Apache License 2.0 — <https://github.com/k2-fsa/sherpa-onnx>
- **sherpa-onnx-streaming-zipformer-en-20M-2023-02-17** — Apache License 2.0 model derived from the linked Icefall model; trained with LibriSpeech data distributed under CC BY 4.0 — <https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-20M-2023-02-17>

The speech model is not stored in source control. `scripts/fetch-local-asr-model.ps1` downloads the official archive and verifies SHA-256 `9C559283E8498D3FE95913C79CA1CB454BB26281AC2B102B41306C7D752765D9`. A `MODEL-NOTICE.txt` file is copied beside every bundled model.
