# Third-party notices

OpenScanner itself is MIT licensed (see `LICENSE`). Release artifacts bundle, and
the installer builds, the third-party software below.

## Bundled in release artifacts

### whisper.cpp
Release tarballs include a `whisper-cli` binary built from
[whisper.cpp](https://github.com/ggerganov/whisper.cpp) (the exact version is in
`whisper/VERSION`). MIT License, Copyright (c) 2023-2026 The ggml authors. The full
licence text ships alongside the binary as `whisper/LICENSE`.

### .NET runtime and NuGet packages
The server is published self-contained, so the tarball includes the
[.NET runtime](https://github.com/dotnet/runtime) (MIT) and the server's NuGet
dependencies: Concentus (BSD-3-Clause), Dapper (Apache-2.0), FftSharp (MIT),
Microsoft.Data.Sqlite (MIT) with SQLite (public domain), and NSwag (MIT).

### Client
The web client bundles its npm dependencies (React, MUI and others) under their
respective licences; see `client/package.json`.

## Built on the device by the installer (not distributed by OpenScanner)

### mbelib
[mbelib](https://github.com/szechyjs/mbelib) is ISC licensed, but its authors
attach this notice:

> This source code is provided for educational purposes only. It is a written
> description of how certain voice encoding/decoding algorithms could be
> implemented. Executable objects compiled or derived from this package may be
> covered by one or more patents. Readers are strongly advised to check for any
> patent restrictions or licensing requirements before compiling or using this
> source code.

For that reason OpenScanner does **not** ship mbelib binaries. `install.sh`
downloads the source and compiles it on your device.

### DSD-FME
[dsd-fme](https://github.com/lwvmobile/dsd-fme) is ISC licensed with several
GPL-2.0 components. It links against mbelib, so it is likewise compiled on the
device rather than shipped. OpenScanner runs it as a separate process.
