# HG-FBG Local Monitor - Multi-Device Edition

Chinese guide: `README_CN.md`

Independent multi-device branch of the local distributed fiber temperature monitor, based on:
- `D:\WorkSpace\DTS\DTS SDK V1.0 20250625\interface.h`
- `RC_FBGSystem.dll`

## Architecture

- `native/`: C++ core (`dts_core.dll`)
  - SDK lifecycle and state machine
  - callback-thread minimal copy + background processing thread
  - converts SDK wavelength samples to temperature profile
  - profile interpolation to target points (default 5000)
  - alarm point extraction and latest snapshot cache
  - C ABI for C# (`dts_core_api.h`)
- `managed/DtsMonitor.App/`: C# WPF (.NET 10)
  - multi-device monitor shell
  - per-device worker process + named-pipe IPC
  - monitor page (wave + alarm list + status)
  - control page (connect/config/run)
  - history page (SQLite alarm query)
  - Native bridge via P/Invoke (`NativeBridge.cs`)
- `managed/DtsMonitor.DeviceWorker/`: per-device worker host
  - owns one SDK session per process
  - persists device alarm history to SQLite

## Native C API (summary)

Defined in `native/include/dts_core_api.h`:
- lifecycle: `DTS_CreateContext`, `DTS_Initialize`, `DTS_Release`, `DTS_DestroyContext`
- config: `DTS_SetBasicConfig`, `DTS_SetWaveConfig`, `DTS_SetChannelConfig`, `DTS_SetRunMode`, `DTS_ApplyHardwareConfig`
- runtime: `DTS_StartCalibration`, `DTS_StopCalibration`, `DTS_StartAcquisition`, `DTS_StopAcquisition`
- pull mode: `DTS_GetLatestSummary`, `DTS_CopyLatestSnapshot`

## SQLite schema

`SnapshotStore` creates:
- `snapshot_index`
- `profile_chunks` (compressed profile chunks)
- `alarm_events`

Default retention: 30 days.

## Build (Windows x64)

## 1) Build native `dts_core.dll`

Prerequisites:
- Visual Studio 2022 C++ toolchain
- CMake 3.20+

Commands (Developer PowerShell):

```powershell
cd D:\WorkSpace\DTS\DTSLocalMonitor\native
cmake -S . -B build -A x64 -DDTS_SDK_ROOT="D:/WorkSpace/DTS/DTS SDK V1.0 20250625"
cmake --build build --config Release
```

Output:
- `native\build\Release\dts_core.dll`
- post-build copies: `RC_FBGSystem.dll`, `opencv_world3412.dll`

## 2) Build WPF app and worker

Prerequisites:
- .NET SDK 10.x

```powershell
cd D:\WorkSpace\DTS\DTSLocalMonitor\managed\DtsMonitor.App
dotnet restore
dotnet build -c Release
```

Copy `dts_core.dll` to app output folder:
- `managed\DtsMonitor.App\bin\Release\net10.0-windows\`

And ensure these are in the same output folder:
- `RC_FBGSystem.dll`
- `opencv_world3412.dll`

## Run

```powershell
cd D:\WorkSpace\DTS\DTSLocalMonitor\managed\DtsMonitor.App\bin\Release\net10.0-windows
dotnet HG-FBG.dll
```

In UI:
1. `Connect`
2. `Apply Config`
3. optional calibration: `Start Calib` / `Stop Calib`
4. `Start Run`

## Notes

- Current temperature conversion uses baseline-delta approximation:
  - `Temp = 25 + (wavelength - baseline) * 120`
  - baseline is captured per sensor from first valid frame
- Replace with your real DTS calibration/temperature model when SDK formula is finalized.
- Multi-device mode isolates SDK state by running one worker process per device.
