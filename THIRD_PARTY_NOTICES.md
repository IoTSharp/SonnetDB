# Third Party Notices

SonnetDB includes selected third-party assets. This file records assets and license terms that are redistributed with the repository or packaged artifacts.

## Server image codecs

Server image processing uses SkiaSharp 4.152.1 and TiffLibrary 0.6.65, with JpegLibrary 0.4.32. Their fixed-source licenses and original bundled notices are preserved in [licenses/image-codecs](licenses/image-codecs/README.md) and copied into publish output. The native asset has its own third-party terms, including Adobe DNG SDK terms, and is not represented as MIT-only. Historical Apache-2.0 Six Labors source attribution in the TIFF/JPEG libraries is retained; it does not introduce the modern Six Labors Split License package or a build-license requirement.

## cppjieba dictionary

- Project: cppjieba
- Upstream: https://github.com/yanyiwu/cppjieba
- Commit: `8f171de5018e8478ff22ca58caacf579cba809c8`
- Files used: `dict/jieba.dict.utf8`, converted to `src/SonnetDB.Core/FullText/Tokenizers/Jieba/Resources/dict.txt`
- License: MIT

```text
The MIT License (MIT)

Copyright (c) 2013

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
