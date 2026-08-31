# ImageGlass Entis ERI Codec Plugin

Native **decode-only** codec plugin for ImageGlass v10 that adds `.eri` / `.emi`
support, ported from the GARbro C# ERI decoder (morkt, based on the ERISa
library by Leshade Entis / Entis-soft).

| Extension | Read | Write |
| --------- | ---- | ----- |
| `.eri` / `.emi` | ✅ Lossless & DCT/LOT lossy variants | ❌ |

Supports: 24/32bpp RGB(A)/BGR images, 8bpp grayscale, 8bpp indexed images with
embedded palettes, YUV 4:4:4 / 4:1:1 chroma subsampling, the `#reference-file`
delta compositing tag, and the `VIST` (Moving Entis Image) header variant.

No SkiaSharp or any third-party dependency — the decoder is pure C#, so the
package stays tiny and fully cross-platform.

## Supported capabilities

| Feature | Status |
| ------- | ------ |
| Lossless_EMI / Lossless_ERI | ✅ |
| DCT_ERI (8×8 DCT) | ✅ |
| LOT_ERI / LOT_ERI_MSS (lapped orthogonal transform) | ✅ |
| Arithmetic / Run-length Gamma / Run-length Huffman / Nemesis entropy coding | ✅ |
| 24bpp (BGR), 32bpp (BGRA / BGR), 8bpp gray, 8bpp indexed + palette | ✅ |
| Multi-frame ERI (`.emi` with `FrameCount > 1`) | ⚠️ first frame only |
| Lossy delta / motion-vector variants | ❌ (not implemented upstream) |

> Some lossy sub-variants (lossy delta compression, motion-vector frames) are
> not implemented in GARbro itself; such files are reported as decode failures
> rather than crashing the viewer.

## How it maps to the ABI

- `LoadMetadata` parses only the section headers (fast thumbnails), reports
  `IGPixelFormat.Bgra8Unorm`, alpha per `EriType.WithAlpha`, sRGB, 1 frame.
- `DecodeStaticRaster` decodes via the ported `EriReader` into BGRA8 and copies
  into a plugin-owned `NativeMemory.Alloc` buffer.
- `CanHandleSignature` sniffs the `Enti` / `VIST` magic in addition to extension
  matching.
- `FreePixelBuffer` only frees buffers recorded in `_liveBuffers` (thread-safe).
- All encode / animation entry points are null; capability flags reflect that.

## Build

```powershell
dotnet publish source/ImageGlassEriCodec.csproj `
    -c Release -r win-x64 -p:Platform=x64 `
    -o out/win-x64
```

(Use `-r linux-x64` / `-r osx-arm64` with matching `-p:Platform` for other targets.)

> Publish, never `dotnet build` — a managed build exports no `ig_plugin_get_api`.

## Package

```powershell
Compress-Archive -Path "out/win-x64/EriCodec.dll", `
    "out/win-x64/igplugin.json" `
    -DestinationPath EriCodec-win-x64.igplugin.zip
```

Install via **Settings > Plugins > Add** (select the zip), then **Trust and enable**.

## License note

Decode algorithm ported from [GARbro](https://github.com/morkt/GARbro)
(MIT), itself based on the ERISa library
(Copyright (C) 2002–2004 Leshade Entis, Entis-soft. All rights reserved).