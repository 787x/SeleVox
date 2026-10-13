# Local OCR assets

- .NET wrapper: [charlesw/Tesseract 5.2.0](https://github.com/charlesw/tesseract), Copyright 2012–2022 Charles Weld, Apache-2.0 (`Tesseract-LICENSE.txt`). NuGet includes native Tesseract 5.2.0 (`x64/tesseract50.dll`) and Leptonica 1.82.0 (`x64/leptonica-1.82.0.dll`). The wrapper targets .NET Standard 2.0 and is exercised on .NET 10 Windows x64 in PR6; upstream does not promise a separate .NET 10 support lifecycle.
- [Tesseract](https://github.com/tesseract-ocr/tesseract/tree/5.2.0), Apache-2.0 (`Native-Tesseract-LICENSE`). Original Tesseract copyright and contributors are described in the upstream source.
- [Leptonica 1.82.0](https://github.com/DanBloomberg/leptonica/tree/1.82.0), Copyright 2001 Dan Bloomberg, BSD-style (`Leptonica-LICENSE.txt`).
- InteropDotNet incorporated by the wrapper: Copyright 2014 Andrey Akinshin, MIT (`InteropDotNet-LICENSE.txt`).
- Language models: [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd), revision `87416418657359cb625c412a48b6e1d6d41c29bd`, Apache-2.0 (`LICENSE`). Use the integer LSTM models with LSTM-only recognition. Models are unmodified and checked into this repository; no runtime download occurs.

| Model | Bytes | SHA256 |
| --- | ---: | --- |
| chi_sim.traineddata | 2469156 | a5fcb6f0db1e1d6d8522f39db4e848f05984669172e584e8d76b6b3141e1f730 |
| eng.traineddata | 4113088 | 7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2 |

Distribute this folder, its notices, and native dependencies together with the application. Microsoft CRT redistribution is described in `OcrRuntime/NOTICE.md`.
