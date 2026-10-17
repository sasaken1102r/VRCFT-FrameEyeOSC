# Third-party notices

## VRCFaceTracking

`VRCFaceTracking.Core.dll` (5.2.3.0) and `VRCFaceTracking.SDK.dll` (1.0.0.0) in [refs/](refs/) are the builds shipped in VRCFT
Avalonia v1.1.1.0, from
[dfgHiatus/VRCFaceTracking](https://github.com/dfgHiatus/VRCFaceTracking) commit `13d051a` (the fork that VRCFT Avalonia
is built from), itself a fork of [benaclejames/VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking). They are
used only to build the module against VRCFaceTracking's SDK: they are not part of `FrameEyeOSC.dll`, are not copied next
to it by the build, and VRCFaceTracking provides its own copies at run time. They are redistributed here unmodified.
Details and hashes are in [refs/README.md](refs/README.md).

Copyright 2024 benaclejames

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these files except in compliance with the
License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0, and the full text, as
VRCFaceTracking ships it (identical in benaclejames/VRCFaceTracking and dfgHiatus/VRCFaceTracking at that commit), is in
[licenses/VRCFaceTracking-LICENSE.txt](licenses/VRCFaceTracking-LICENSE.txt).

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific
language governing permissions and limitations under the License.

VRCFaceTracking ships no NOTICE file.
