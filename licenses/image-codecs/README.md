# Image codec notices

SonnetDB uses SkiaSharp 4.152.1 and TiffLibrary 0.6.65 (JpegLibrary 0.4.32) in the Server image boundary. The original notices in this directory are copied into Server, Studio's bundled Server, container and image sample publish output. They do not require a licensing account, commercial activation key or build credential. They remain separate from SonnetDB's MIT license.

| Component | Fixed source | License / redistribution record |
| --- | --- | --- |
| SkiaSharp and its native assets | `mono/SkiaSharp@47f1630989b6d4d5c347b01dcfc10f0fb49cce74` | MIT; retain the package's complete native third-party notice. |
| Skia | `mono/skia@723c5a03a7d18de5fb1a041e6cae2f09c4cb3c7e` | BSD and the bundled codec notices; the entire native binary must not be described as MIT-only. |
| TiffLibrary | `yigolden/TiffLibrary@32f43490e4140563ae380799dd080ca57881daeb` | MIT plus the unmodified upstream third-party notice. |
| JpegLibrary | `yigolden/JpegLibrary@d4dcb149dd4cd4a18994bf5bf9009f5691e39e75` | MIT plus the unmodified upstream third-party notice. |
| Wuffs (current Skia GIF codec) | `google/wuffs@e3f919ccfe3ef542cfc983a82146070258fb57f8` | Apache-2.0; fixed-source license included. |
| Adobe DNG SDK (bundled native code) | `dbe0a676450d9b8c71bf00688bb306409b779e90` | Original license included. It grants royalty-free use and distribution without activation, but includes notice and commercial-distribution indemnification terms; it is not labeled MIT/BSD. |

The native package's `THIRD-PARTY-NOTICES.txt` is a combined upstream record, preserved without deleting entries. Its historical Mozilla GIF notice does not describe the current Wuffs GIF decoder. The historical libmicrohttpd entry relates to `skiaserve`; it is absent from this fixed Skia dependency graph and is not a dependency of the shipped SkiaSharp target. Keeping this combined notice does not mean every historic component is included in the current native library.

TiffLibrary's LZW implementation and JpegLibrary's DCT implementation include **historical Apache-2.0 ImageSharp source**, not the modern Split License package. Original Six Labors copyright notices are intentionally retained. The former points to `SixLabors/ImageSharp@9b8d160faf839e681615924a7644e0e8024c99c2`; the latter references `v1.0.0-beta7`. Both versions' LICENSE files were checked as Apache-2.0. No SixLabors NuGet package, licensing task or modern license key is used. SharpZipLib material in TiffLibrary is MIT.

Source evidence:

- https://github.com/mono/SkiaSharp/tree/v4.152.1
- https://github.com/mono/skia/tree/723c5a03a7d18de5fb1a041e6cae2f09c4cb3c7e
- https://github.com/yigolden/TiffLibrary/tree/32f43490e4140563ae380799dd080ca57881daeb
- https://github.com/yigolden/JpegLibrary/tree/d4dcb149dd4cd4a18994bf5bf9009f5691e39e75
- https://github.com/SixLabors/ImageSharp/blob/9b8d160faf839e681615924a7644e0e8024c99c2/LICENSE
- https://github.com/SixLabors/ImageSharp/blob/v1.0.0-beta7/LICENSE

The native notice is copied from `SkiaSharp.NativeAssets.Linux.NoDependencies/4.152.1/THIRD-PARTY-NOTICES.txt` (identical to the Win32 package); SHA-256: `21504C46C4C58AA64C1055BD2DCBC5F9A136B4B8C412ED3CC6740E22C5B127F5`. Dependency upgrades must recheck actual native components, source notices and NativeAOT smoke results, rather than relying only on the NuGet license-expression field.
