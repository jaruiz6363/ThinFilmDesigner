# Installing and building

There are three ways to get the program, from least to most work:

1. **Install it.** A Windows installer, a macOS zip for Apple Silicon and a Linux AppImage. Nothing
   to build.
2. **Build the C# code with the ready-made engine.** The calculation engine is C++, and a built
   copy of it is provided for each platform, so only the .NET part needs building.
3. **Build everything,** the C++ engine included.

## 1. Installing

The downloads are on the [release page](https://github.com/jaruiz6363/ThinFilmDesigner/releases/latest),
with `SHA256SUMS.txt` to check them.

| Platform | Download | Then |
|---|---|---|
| Windows | the installer (`.exe`) | Run it. It installs the GUI, `thinfilm` (the command line) and `thinfilm-mcp` (the MCP server) in `C:\Program Files\ThinFilmDesigner`, and can add that folder to your PATH. |
| macOS on Apple Silicon (M1 and later, macOS 11 or later) | the `.zip` | Unzip it and drag `ThinFilmDesigner.app` into Applications. The command line and the MCP server are inside it, in `ThinFilmDesigner.app/Contents/MacOS/`. |
| Linux | the `.AppImage` | `chmod +x` it and run it. `--cli` runs the command line instead of the GUI, and `--mcp` the MCP server. |

The downloads include everything they need, the .NET runtime among them.

**Apple Silicon is supported natively.** The macOS app, its command line, its MCP server and the
engine are all built for Apple Silicon (arm64), not for Intel and run under Rosetta. Each macOS
package is tested on an Apple Silicon Mac before release: the command line must evaluate a sample
design exactly as Windows does, the MCP server must answer, and the GUI must open a design.

**Intel Macs** have no ready-made package. The program can be built from source for them (section 3),
but that hasn't been tested.

**macOS, the first time:** the app is signed but not notarised by Apple, so macOS says it
cannot verify the developer. Control-click the app, choose **Open**, then **Open** again; after
that a double-click is enough. If macOS says the app is damaged, the download was quarantined:
run `xattr -dr com.apple.quarantine /Applications/ThinFilmDesigner.app` in Terminal.

## What you need to build

| | Windows | macOS | Linux |
|---|---|---|---|
| **.NET SDK** | 8.0 or later | 8.0 or later | 8.0 or later |
| **CMake** (only to build the engine) | 3.20 or later | 3.20 or later | 3.20 or later |
| **C++17 compiler** (only to build the engine) | Visual Studio 2022 (MSVC) | Xcode Command Line Tools (Clang) | GCC or Clang |

- .NET SDK: https://dotnet.microsoft.com/download. On Ubuntu, `sudo apt install dotnet-sdk-8.0`.
- CMake: https://cmake.org/download, or `brew install cmake`, or `sudo apt install cmake`.
- Compiler: on Windows, Visual Studio 2022 with *Desktop development with C++*; on macOS,
  `xcode-select --install`; on Ubuntu, `sudo apt install build-essential`.

The GUI's library, Avalonia, comes from NuGet the first time you build, so the first build needs
an internet connection. Nothing else is downloaded.

**Tested:** the build and every test, on Windows 11 (Visual Studio 2022, MSVC 19.44, .NET SDK
9.0) and on Ubuntu 24.04 (GCC 13.3, CMake 3.28, .NET SDK 8.0). The macOS package is built for Apple
Silicon and tested on an Apple Silicon Mac (above); building from source on a Mac uses the same
commands with Apple's Clang. The engine is compiled with warnings treated as errors; if a compiler we have not
tried warns about something, please report it.

## Getting the source

```sh
git clone https://github.com/jaruiz6363/ThinFilmDesigner.git
cd ThinFilmDesigner
```

Every command below is run from this top folder.

## 2. Building with the ready-made engine

The engine is one library:

| Platform | File |
|---|---|
| Windows | `thinfilm_native.dll` |
| macOS | `libthinfilm_native.dylib` |
| Linux | `libthinfilm_native.so` |

Download the one for your platform from the
[release page](https://github.com/jaruiz6363/ThinFilmDesigner/releases/latest), and put it in a folder called `build` in
the top folder (create it). The .NET build looks for it there, in `build` or any folder inside
it, and copies it next to each program. Then:

```sh
dotnet build managed/ThinFilm.sln -c Release
```

## 3. Building everything

**The engine,** into the `build` folder (the .NET build looks there):

```sh
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

`-DCMAKE_BUILD_TYPE` is for Linux and macOS; `--config` is for Visual Studio. Giving both is
harmless. This builds the engine library and its 22 test programs. To build the library alone,
add `--target thinfilm_shared` to the second line.

**The .NET programs:**

```sh
dotnet build managed/ThinFilm.sln -c Release
```

## Running what you built

| Program | Windows | macOS and Linux |
|---|---|---|
| GUI | `managed\ThinFilm.Gui\bin\Release\net8.0\thinfilm-gui.exe` | `managed/ThinFilm.Gui/bin/Release/net8.0/thinfilm-gui` |
| Command line | `managed\ThinFilm.Cli\bin\Release\net8.0\thinfilm.exe` | `managed/ThinFilm.Cli/bin/Release/net8.0/thinfilm` |
| MCP server | `managed\ThinFilm.Mcp\bin\Release\net8.0\thinfilm-mcp.exe` | `managed/ThinFilm.Mcp/bin/Release/net8.0/thinfilm-mcp` |
| MCP setup (connects the server to Claude) | `managed\ThinFilm.McpSetup\bin\Release\net8.0\thinfilm-mcp-setup.exe` | `managed/ThinFilm.McpSetup/bin/Release/net8.0/thinfilm-mcp-setup` |

Each program's folder holds everything it needs: the engine library, the sample designs, the
material database and the glass catalogues. See [cli.md](cli.md) for the command line and
[mcp.md](mcp.md) for connecting the MCP server to an AI assistant.

On Linux the GUI needs an X11 or Wayland desktop and the usual libraries
(`sudo apt install libx11-6 libice6 libsm6 libfontconfig1`); the command line and the MCP server
need nothing more.

## Running the tests

```sh
ctest --test-dir build -C Release                                # the engine: 22 test programs
dotnet run --project managed/ThinFilm.Native.Tests -c Release   # API, optimisers, CLI, MCP
dotnet run --project managed/ThinFilm.Gui.Tests -c Release      # the GUI, without a screen
```

Each C# suite ends with a line such as `=== 3009 checks, 0 failures ===`. What the tests check
is described in [verification.md](verification.md).

## Where the program keeps your files

| | Windows | macOS | Linux |
|---|---|---|---|
| Your material database and settings | `%APPDATA%\ThinFilm` | `~/Library/Application Support/ThinFilm` | `~/.config/ThinFilm` |
| Optimisation logs | `%LOCALAPPDATA%\ThinFilm\logs` | `~/Library/Application Support/ThinFilm/logs` | `~/.local/share/ThinFilm/logs` |

`thinfilm database` prints where your material database is. Set the environment variable
`THINFILM_HOME` to keep them all somewhere else.

## Layout of the source

| Folder | What is in it |
|---|---|
| `native/` | The C++ engine. |
| `managed/` | The C# code: `ThinFilm.Native` (the engine's binding), `ThinFilm.Optimize` (the optimisers), `ThinFilm.Api` (the API the three programs share), `ThinFilm.Cli`, `ThinFilm.Mcp`, `ThinFilm.Gui` and `ThinFilm.Gui.Core`, and the two test suites. |
| `tests/` | The engine's tests, and fixtures both sides read. |
| `samples/` | Worked examples, and the designs from the literature. |
| `materials/` | The material database and the glass catalogues. |
| `docs/` | These documents, the user guide, and the design documents. |
| `tools/` | Checks run by the build, and the Optiland comparison script. |
