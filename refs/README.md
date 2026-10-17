# Reference DLLs

`VRCFaceTracking.Core.dll` (5.2.3.0) and `VRCFaceTracking.SDK.dll` (1.0.0.0) are what the module is compiled against.
They are not copied into the module's output; VRCFT provides its own copies at run time.

Source: the two DLLs shipped in the VRCFT Avalonia v1.1.1.0 Linux AppImage (`usr/bin/`), unmodified; the byte-identical
hashes below were checked against that AppImage. The source is
[dfgHiatus/VRCFaceTracking](https://github.com/dfgHiatus/VRCFaceTracking) commit `13d051a`. (Apache-2.0, see
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).) To refresh them, take the same two files from a newer release.

SHA256:

```
794752a141d350e13001baf84aae9cd905fd60e689e8716a5e8e95b66624a23a  VRCFaceTracking.Core.dll
cf7b6815086fcd6e8c05cb336c7368b4367df8f418c1d7487d405d9da86847b8  VRCFaceTracking.SDK.dll
```

Why these and not the benaclejames 5.4.5.0 builds: the module then loads in both VRCFT Avalonia (net8, Core 5.2.3.0)
and benaclejames' VRCFT. The module only uses the Core/SDK types both builds share.
