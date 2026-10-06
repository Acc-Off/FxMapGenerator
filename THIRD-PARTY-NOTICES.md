# Third-Party Notices

FxMapGenerator is distributed under the MIT License. It bundles or uses the third-party works listed below. This file is embedded in the executable and shown in the application.

## Backend (.NET)

| Component | License | URL | Notes |
|---|---|---|---|
| .NET runtime / ASP.NET Core / Windows Forms | MIT (below) | https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore, https://github.com/dotnet/winforms | Kestrel serves the UI on 127.0.0.1; Windows Forms shows the folder and file dialogs. Inside the self-contained executable; what the runtime itself carries is listed in https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT |
| gta-toolkit (`RageLib`, `RageLib.GTA5`) by carmineos | MIT (below) | https://github.com/carmineos/gta-toolkit | Vendored in `third_party/gta-toolkit` (RSC7 resources, texture dictionaries, RPF archives) |
| SkiaSharp | MIT (below) | https://github.com/mono/SkiaSharp | Drawing and PNG encoding |
| Skia (native library bundled by SkiaSharp) | BSD-3-Clause (below) | https://skia.org/ | What the native library itself carries is listed in https://github.com/mono/SkiaSharp/blob/main/External-Dependency-Info.txt |
| BCnEncoder.NET by Nominom | MIT (below; dual-licensed MIT OR Unlicense, used under MIT) | https://github.com/Nominom/BCnEncoder.NET | DXT1 / DXT5 compression of the minimap textures |
| CommunityToolkit.HighPerformance (via BCnEncoder.NET) | MIT (below) | https://github.com/CommunityToolkit/dotnet | |

## Bundled data

| Data | Source | License | Where |
|---|---|---|---|
| Surface material names by hash (`data/materials.json`) | ScriptHookVDotNet `MaterialHash.cs`, https://github.com/scripthookvdotnet/scripthookvdotnet | zlib | Naming the materials the scans report; the material classes (`data/material-classes.json`) are this project's own |
| The route numbers written into street names (`data/routes.json`) | GTA Wiki, "Numbered Highways in GTA V": which of the game's streets carry which route number (facts; the texts are this project's own) | Facts, credited in the atlas maps' credits | The labels |
| The blocks of the Roxwood range preset (`data/range-presets.json`: which z8 blocks hold Roxwood's land, an estimate) | The land drawn in the Roxwood minimap resource by RRixxles (Roxwood-Minimap, https://github.com/RRixxles/Roxwood-Minimap), compared with the standard map | Facts (block numbers); no picture or file of the resource is bundled. The screen and the README say it is an estimate from that minimap | The **Add a preset** tool of the range |
| The icons of the POI styles (`data/mdi-icons.json`: the path data, aliases and tags of the icons that are not deprecated) | Material Design Icons by Pictogrammers, `@mdi/svg` 7.4.47, https://pictogrammers.com/library/mdi/ (https://github.com/Templarian/MaterialDesign-SVG) | Pictogrammers Free License: the icons under the Apache License 2.0 (below) | The POI styles' icons, on the POI screen and drawn on the maps |

## Algorithms adapted from other projects

Parts of the own grid and geometry code follow these projects' code closely, down to the order of the operations, so
that they give the same results. All of it is written anew in C#; the comments of each file name the functions it
follows and what it leaves out. The files are under `src/FxMapGenerator.Core/`.

| Source | License | What follows it | Where |
|---|---|---|---|
| SciPy, `scipy.ndimage`: `distance_transform_edt` (its feature transform), and of SciPy 1.17 `gaussian_filter` and `map_coordinates`. https://github.com/scipy/scipy | BSD-3-Clause (below) | The nearest-feature transform, Gaussian blurs, linear samples between cells. Changes: written in C# for 2-D grids | `Grids/DistanceTransform.cs`, `Grids/Gaussian.cs` (`Scipy`), `Grids/Sampling.cs` |
| OpenCV: `getStructuringElement(MORPH_ELLIPSE)`, and of OpenCV 5.0.0 `polylines` (`modules/imgproc/src/drawing.cpp`) and `GaussianBlur` (with `f64_exp` of `modules/core/src/softfloat.cpp`). https://github.com/opencv/opencv | Apache-2.0 (below); `drawing.cpp` also carries the Intel notice (below); `softfloat.cpp` is based on the SoftFloat package (BSD-3-Clause, below) | The elliptic structuring element, thick polylines, Gaussian blurs. Changes: written in C# for masks and float64 grids; polylines for thicknesses of 2 and more only | `Grids/Kernel.cs`, `Grids/ThickLines.cs`, `Grids/Gaussian.cs` (`OpenCv`, `CvKernel`, `CvExp`) |
| CPython 3.11: `math.hypot`, the floor division of floats, `set` and the hash of tuples. https://github.com/python/cpython | PSF-2.0 (below) | The length of a vector of two values, floor division, the order a set of pixel pairs is walked in. Changes: written in C# for two values and for pairs of whole numbers | `Geometry/Num.cs` (`Hypot`, `PyFloorDiv`), `Labels/PixelSet.cs` |
| NumPy: `interp`, the pairwise summation of `sum`, `linspace`, `arange`. https://github.com/numpy/numpy | BSD-3-Clause (below) | Linear interpolation between sample points, the sum of an array, evenly spaced values. Changes: written in C# for double values | `Geometry/Num.cs` (`Interp`, `NpInterp`, `NpSum`, `Linspace`, `NpArange`) |
| Pillow 10.4.0: `ImageDraw.polygon`, `ImageDraw.line` (`src/libImaging/Draw.c`). https://github.com/python-pillow/Pillow | HPND (MIT-CMU, below) | Filled polygons and lines. Changes: written in C# for masks | `Grids/PilDraw.cs` |
| scikit-image 0.26.0: `skeletonize`. https://github.com/scikit-image/scikit-image | BSD-3-Clause (below) | Thinning to lines one cell wide. Changes: written in C# | `Grids/Skeleton.cs` |
| contourpy 1.3.3: the "serial" generator (`src/base_impl.h`, `src/serial.cpp`). https://github.com/contourpy/contourpy | BSD-3-Clause (below) | Contour lines. Changes: written in C# for lines without a mask in one chunk | `Vectors/Contours.cs` |

## Frontend (npm, bundled into the executable)

| Package | License | URL |
|---|---|---|
| React / React DOM (with scheduler, which React DOM uses) | MIT (below) | https://github.com/facebook/react |
| Zustand | MIT (below) | https://github.com/pmndrs/zustand |
| Leaflet | BSD-2-Clause (below) | https://github.com/Leaflet/Leaflet |
| driver.js (the screens' guides) | MIT (below) | https://github.com/nilbuild/driver.js |

Build tools (Vite, TypeScript, esbuild, Rollup) are not part of the distributed application.

The PostalMap base map of the screens (the project screen, the map view, the road and POI editors and the style
preview) is loaded at run time from https://postal-code-map-tile.pages.dev (the postal code map tiles); it is not
bundled.

The postal codes of the atlas maps are fetched at run time, unless the project gives its own, from nearest-postal's
table for the postal code map (https://github.com/DevBlocky/nearest-postal, `new-postals.json`, MIT License); they are not bundled. The atlas maps'
credits name the source.

## Written into exports

| Part | Source | License / terms | Where |
|---|---|---|---|
| Leaflet 1.9.4 (`leaflet.js`, `leaflet.css`) | https://github.com/Leaflet/Leaflet | BSD-2-Clause (below); its license goes into `web/leaflet/LICENSE.txt` | `data/viewer/`, the web export's viewer |
| The bundled points of interest: the positions of the 26 Highway One markers A-Z, and the positions, colours and sizes of the 5 coloured dots | The postal code map by Virus_City (the map picture of the first release): https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458 | The author allows releasing versions of it with credit; the atlas maps drawn with them credit Virus_City | `data/poi/highway-markers.json`, `data/poi/colored-dots.json`, `data/poi-styles.json` |
| The look of the bundled atlas style "PostalCodeMap style" (its colours, widths and labels follow the map picture) | The postal code map by Virus_City: https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458 | The author allows releasing versions of it with credit; the style's `credit` puts Virus_City into the credits of the maps drawn with it | `data/styles/postalcodemap.json` |
| The sea of the bundled atlas style "Regional colors" (the colours and opacity of its depth bands, measured from the map picture; the same values as the "PostalCodeMap style") | The postal code map by Virus_City: https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458 | The author allows releasing versions of it with credit; the style's `credit` puts Virus_City into the credits of the maps drawn with it | `data/styles/regional.json` |
| 65 empty minimap drawables (`minimap_<r>_<c>.ydd`) and the zoom values | The postal code map minimap by Virus_City, first release: https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458 | The author allows releasing versions of it with credit; the minimap resource's `CREDITS.txt` names Virus_City | `data/minimap/`, the minimap resource |
| The icons the POI styles draw | Material Design Icons by Pictogrammers (see "Bundled data") | Apache License 2.0 (below) | The atlas maps and the road map (web tiles, minimap) where a POI style draws an MDI icon |
| Extra Map Tiles 3.0.1 (`client.lua`, `exports.lua`, `scaleforms.lua`, `utils.lua`, `stream/minimap_main_map.gfx`, `stream/radar_masks.ytd`) | Alex Licuriceanu (L1CKS), https://github.com/alexlicuriceanu/extra-map-tiles, the release zip of tag v3.0.1 | MIT (below); its license goes into the resource as `extra-map-tiles-LICENSE.txt`, and the resource's `CREDITS.txt` names it | `data/minimap/extra-map-tiles/`, the minimap resource (draws the map outside the standard map) |

## License texts

### MIT License

The parts below are under the MIT License. Its text is the same for all of them and stands once, under their copyright
notices.

```
.NET runtime, ASP.NET Core, Windows Forms
    Copyright (c) .NET Foundation and Contributors
    All rights reserved.

gta-toolkit
    Copyright © Neodymium, carmineos and contributors

SkiaSharp
    Copyright (c) 2015-2016 Xamarin, Inc.
    Copyright (c) 2017-2018 Microsoft Corporation.

BCnEncoder.NET
    Copyright 2020 Nomi Lakkala

.NET Community Toolkit (CommunityToolkit.HighPerformance)
    Copyright © .NET Foundation and Contributors
    All rights reserved.

React, React DOM, scheduler
    Copyright (c) Meta Platforms, Inc. and affiliates.

Zustand
    Copyright (c) 2019 Paul Henschel

driver.js
    Copyright (c) Kamran Ahmed

Extra Map Tiles
    Copyright (c) 2026 Alex Licuriceanu
```

```
MIT License

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

### Leaflet

```
BSD 2-Clause License

Copyright (c) 2010-2023, Volodymyr Agafonkin
Copyright (c) 2010-2011, CloudMade
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Skia

```
Copyright (c) 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of the copyright holder nor the names of its
    contributors may be used to endorse or promote products derived
    from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### SciPy

```
Copyright (c) 2001-2002 Enthought, Inc. 2003, SciPy Developers.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above
   copyright notice, this list of conditions and the following
   disclaimer in the documentation and/or other materials provided
   with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived
   from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### OpenCV

OpenCV is copyright its contributors (the holders are listed in its COPYRIGHT file, https://github.com/opencv/opencv/blob/4.x/COPYRIGHT) and licensed under the Apache License 2.0:

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS
```

The drawing code (`modules/imgproc/src/drawing.cpp`) also carries this notice:

```
                       Intel License Agreement
               For Open Source Computer Vision Library

Copyright (C) 2000, Intel Corporation, all rights reserved.
Third party copyrights are property of their respective owners.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

  * Redistribution's of source code must retain the above copyright notice,
    this list of conditions and the following disclaimer.

  * Redistribution's in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

  * The name of Intel Corporation may not be used to endorse or promote products
    derived from this software without specific prior written permission.

This software is provided by the copyright holders and contributors "as is" and
any express or implied warranties, including, but not limited to, the implied
warranties of merchantability and fitness for a particular purpose are disclaimed.
In no event shall the Intel Corporation or contributors be liable for any direct,
indirect, incidental, special, exemplary, or consequential damages
(including, but not limited to, procurement of substitute goods or services;
loss of use, data, or profits; or business interruption) however caused
and on any theory of liability, whether in contract, strict liability,
or tort (including negligence or otherwise) arising in any way out of
the use of this software, even if advised of the possibility of such damage.
```

### CPython

```
PYTHON SOFTWARE FOUNDATION LICENSE VERSION 2
--------------------------------------------

1. This LICENSE AGREEMENT is between the Python Software Foundation
("PSF"), and the Individual or Organization ("Licensee") accessing and
otherwise using this software ("Python") in source or binary form and
its associated documentation.

2. Subject to the terms and conditions of this License Agreement, PSF hereby
grants Licensee a nonexclusive, royalty-free, world-wide license to reproduce,
analyze, test, perform and/or display publicly, prepare derivative works,
distribute, and otherwise use Python alone or in any derivative version,
provided, however, that PSF's License Agreement and PSF's notice of copyright,
i.e., "Copyright (c) 2001, 2002, 2003, 2004, 2005, 2006, 2007, 2008, 2009, 2010,
2011, 2012, 2013, 2014, 2015, 2016, 2017, 2018, 2019, 2020, 2021, 2022, 2023 Python Software Foundation;
All Rights Reserved" are retained in Python alone or in any derivative version
prepared by Licensee.

3. In the event Licensee prepares a derivative work that is based on
or incorporates Python or any part thereof, and wants to make
the derivative work available to others as provided herein, then
Licensee hereby agrees to include in any such work a brief summary of
the changes made to Python.

4. PSF is making Python available to Licensee on an "AS IS"
basis.  PSF MAKES NO REPRESENTATIONS OR WARRANTIES, EXPRESS OR
IMPLIED.  BY WAY OF EXAMPLE, BUT NOT LIMITATION, PSF MAKES NO AND
DISCLAIMS ANY REPRESENTATION OR WARRANTY OF MERCHANTABILITY OR FITNESS
FOR ANY PARTICULAR PURPOSE OR THAT THE USE OF PYTHON WILL NOT
INFRINGE ANY THIRD PARTY RIGHTS.

5. PSF SHALL NOT BE LIABLE TO LICENSEE OR ANY OTHER USERS OF PYTHON
FOR ANY INCIDENTAL, SPECIAL, OR CONSEQUENTIAL DAMAGES OR LOSS AS
A RESULT OF MODIFYING, DISTRIBUTING, OR OTHERWISE USING PYTHON,
OR ANY DERIVATIVE THEREOF, EVEN IF ADVISED OF THE POSSIBILITY THEREOF.

6. This License Agreement will automatically terminate upon a material
breach of its terms and conditions.

7. Nothing in this License Agreement shall be deemed to create any
relationship of agency, partnership, or joint venture between PSF and
Licensee.  This License Agreement does not grant permission to use PSF
trademarks or trade name in a trademark sense to endorse or promote
products or services of Licensee, or any third party.

8. By copying, installing or otherwise using Python, Licensee
agrees to be bound by the terms and conditions of this License
Agreement.
```

### NumPy

```
Copyright (c) 2005-2024, NumPy Developers.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer.

    * Redistributions in binary form must reproduce the above
       copyright notice, this list of conditions and the following
       disclaimer in the documentation and/or other materials provided
       with the distribution.

    * Neither the name of the NumPy Developers nor the names of any
       contributors may be used to endorse or promote products derived
       from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Pillow

```
The Python Imaging Library (PIL) is

    Copyright © 1997-2011 by Secret Labs AB
    Copyright © 1995-2011 by Fredrik Lundh and contributors

Pillow is the friendly PIL fork. It is

    Copyright © 2010-2024 by Jeffrey A. Clark and contributors

Like PIL, Pillow is licensed under the open source HPND License:

By obtaining, using, and/or copying this software and/or its associated
documentation, you agree that you have read, understood, and will comply
with the following terms and conditions:

Permission to use, copy, modify and distribute this software and its
documentation for any purpose and without fee is hereby granted,
provided that the above copyright notice appears in all copies, and that
both that copyright notice and this permission notice appear in supporting
documentation, and that the name of Secret Labs AB or the author not be
used in advertising or publicity pertaining to distribution of the software
without specific, written prior permission.

SECRET LABS AB AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
SOFTWARE, INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS.
IN NO EVENT SHALL SECRET LABS AB OR THE AUTHOR BE LIABLE FOR ANY SPECIAL,
INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

### scikit-image

```
Copyright: 2009-2022 the scikit-image team
License: BSD-3-Clause

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
3. Neither the name of the University nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE HOLDERS OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### contourpy

```
BSD 3-Clause License

Copyright (c) 2021-2025, ContourPy Developers.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### SoftFloat (the base of OpenCV's softfloat.cpp)

```
This C source file is part of the SoftFloat IEEE Floating-Point Arithmetic
Package, Release 3c, by John R. Hauser.

Copyright 2011, 2012, 2013, 2014, 2015, 2016, 2017 The Regents of the
University of California.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

 1. Redistributions of source code must retain the above copyright notice,
    this list of conditions, and the following disclaimer.

 2. Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions, and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

 3. Neither the name of the University nor the names of its contributors may
    be used to endorse or promote products derived from this software without
    specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS "AS IS", AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE, ARE
DISCLAIMED.  IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Material Design Icons

The license of `@mdi/svg` (its `LICENSE` file); the Apache License 2.0 is quoted in full under OpenCV above:

```
Pictogrammers Free License
--------------------------

This icon collection is released as free, open source, and GPL friendly by
the [Pictogrammers](http://pictogrammers.com/) icon group. You may use it
for commercial projects, open source projects, or anything really.

# Icons: Apache 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
Some of the icons are redistributed under the Apache 2.0 license. All other
icons are either redistributed under their respective licenses or are
distributed under the Apache 2.0 license.

# Fonts: Apache 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
All web and desktop fonts are distributed under the Apache 2.0 license. Web
and desktop fonts contain some icons that are redistributed under the Apache
2.0 license. All other icons are either redistributed under their respective
licenses or are distributed under the Apache 2.0 license.

# Code: MIT (https://opensource.org/licenses/MIT)
The MIT license applies to all non-font and non-icon files.
```

## Not included

- **GTA V game data** and the **RPF decryption keys** are never bundled. The application reads the user's own installation; data it takes from the game (road network, names) stays in the user's work folder and is not distributed. One file of the game goes into what the application writes: when a project reads Cayo Perico's roads, the minimap resource it exports carries the game's island map (`int3232302352.gfx`), read from the user's own installation at the export, with its shapes made to draw nothing. It is in no release of the application.
