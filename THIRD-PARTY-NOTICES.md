# Third-party notices

Rambutan Music Player is licensed under the GNU General Public License,
version 3 or later (see `LICENSE`). It includes the components below, under
their own licenses. Each component's notice is reproduced here or in the
`licenses` folder that ships beside this file.

## Components

| Component | License | Notice |
|---|---|---|
| .NET runtime, Microsoft.Extensions.DependencyInjection, System.Security.Cryptography.ProtectedData | MIT | below; its own third-party notices in `licenses/dotnet-runtime.txt` |
| ASP.NET Core (Kestrel, for the phone remote and the OBS overlay) | MIT | below; its own third-party notices in `licenses/aspnetcore.txt` |
| Avalonia | MIT | below; its own third-party notices in `licenses/avalonia.md` |
| ANGLE (`av_libglesv2.dll`, Windows only) | BSD-3-Clause | `licenses/angle.txt` |
| SkiaSharp and HarfBuzzSharp, with the native Skia and HarfBuzz libraries | MIT | below; the native libraries' notices in `licenses/skiasharp-harfbuzzsharp.txt` |
| NWayland | MIT | below |
| MicroCom.Runtime | MIT | below |
| CommunityToolkit.Mvvm | MIT | below |
| Microsoft.IO.RecyclableMemoryStream | MIT | below |
| SoundFlow, with miniaudio | MIT | `licenses/soundflow.txt` |
| FFmpeg.AutoGen | MIT | below |
| Tmds.DBus.Protocol | MIT | below |
| Velopack | MIT | below |
| Svelte (compiled into the phone remote and the OBS overlay) | MIT | below |
| Inter font | SIL Open Font License 1.1 | `licenses/inter.txt` |
| Figtree font | SIL Open Font License 1.1 | `licenses/figtree.txt` |

## Downloaded at runtime, not included

Rambutan downloads two programs on first use and keeps them in its data
folder. They are not part of this distribution:

- **FFmpeg**, the LGPL shared build from https://github.com/BtbN/FFmpeg-Builds,
  under the GNU Lesser General Public License (https://ffmpeg.org/legal.html).
- **yt-dlp**, from https://github.com/yt-dlp/yt-dlp, under the Unlicense.

## MIT License notices

Each component below is licensed under the MIT License, with the copyright
notice shown. The license text follows the list.

- .NET runtime, Microsoft.Extensions.DependencyInjection,
  System.Security.Cryptography.ProtectedData, ASP.NET Core:
  Copyright (c) .NET Foundation and Contributors
- Avalonia: Copyright (c) AvaloniaUI OÜ
- SkiaSharp, HarfBuzzSharp: Copyright (c) 2015-2016 Xamarin, Inc.;
  Copyright (c) 2017-2018 Microsoft Corporation.
- NWayland: Copyright (c) 2014 Steven Kirk
- MicroCom.Runtime: Copyright (c) 2021 Nikita Tsukanov
- CommunityToolkit.Mvvm: Copyright © .NET Foundation and Contributors
- Microsoft.IO.RecyclableMemoryStream: Copyright (c) 2015-2016 Microsoft
- FFmpeg.AutoGen: Copyright (c) 2025 Ruslan Balanukhin (Rationale One)
- Tmds.DBus.Protocol: Copyright 2006 Alp Toker <alp@atoker.com>;
  Copyright 2010 Other Contributors; Copyright 2016 Tom Deseyn <tom.deseyn@gmail.com>
- Velopack: Copyright © 2021 Caelan Sayler; Copyright © 2024 Velopack Ltd.
- Svelte: Copyright (c) 2016-2025 Svelte Contributors
  (https://github.com/sveltejs/svelte/graphs/contributors)

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

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
