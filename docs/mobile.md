# Mobile smoke (Android and iOS)

`RecursiveAI.OneMedia` and `RecursiveAI.OneMedia.Audio` run trimmed (Release, `PublishTrimmed`, `TrimMode=full`) on Android and iOS.
Two small apps prove it: `smoke/OneMedia.Android` and `smoke/OneMedia.iOS`. Each has no UI; it runs the same checks as the NativeAOT
smoke app (`smoke/Shared`, 197 checks) plus four phone-app checks (`smoke/Mobile/MobileChecks.cs`), logs one line per check and ends
with `mobile smoke: ok` or `mobile smoke: FAILED`:

- `SaveAs` of a title edit to a file in the app's own storage, then the title read back;
- the audio track's packet payload hash (SHA-256 over the packets in order) is the same before and after the edit;
- a remux of the M4A to Matroska with the same payload hash;
- the AAC audio decoded to PCM through the `.Audio` adapter (6,400 frames, all samples finite).

Both apps are `IsPackable=false`, are not in `OneMedia.slnx` and are not built by CI: hosted runners would need the Android and iOS
workloads, an emulator with KVM and a macOS simulator. The scripts are run by hand at milestones.

## Running

- **Android** (Linux x64 with `/dev/kvm`): `smoke/OneMedia.Android/run-emulator.sh`. Needs a .NET 10 SDK with the `android` workload, a
  JDK 17 or newer in `JAVA_HOME`, curl and unzip. It fetches the Android command-line tools, emulator and an API 36 x86_64 system image
  into `$ONEMEDIA_MOBILE` (default `~/onemedia/mobile`; running it accepts the SDK licences for that directory), builds, boots a headless
  emulator, installs the APK, waits for the result line in logcat and shuts the emulator down. Exit 0 only on `mobile smoke: ok`.
  `BUILD_ONLY=1` stops after the build. For a device, build with `-p:RuntimeIdentifier=android-arm64`.
- **iOS** (macOS on Apple silicon): `smoke/OneMedia.iOS/run-simulator.sh`. Needs Xcode with an iOS simulator runtime and a .NET 10 SDK
  with the `ios` workload (`IOS_VALIDATE_XCODE=false` if the workload does not list the installed Xcode). It builds, creates a fresh
  simulator, installs and launches the app, reads `Documents/smoke.txt` from the app container and deletes the simulator. Exit 0 only on
  `mobile smoke: ok`. (The simulator's console capture does not see .NET console output, so the app mirrors it into that file.)

## What the iOS app needs

The iOS build interprets what the AOT compiler leaves out (`UseInterpreter`), and keeps a conventional `bin/obj` tree
(`smoke/OneMedia.iOS/Directory.Build.props`) because the shared artifacts layout breaks the runtime's dynamic libraries in the bundle.
Current iOS aborts an app without the scene lifecycle, so the app has the smallest scene delegate.

## Last run (2026-11-02)

| | Android | iOS |
|---|---|---|
| Target | Android 16, API 36, `sdk_gphone64_x86_64` emulator (x86_64, KVM) | iOS 27.1 simulator, iPhone, arm64 |
| .NET SDK / workload | 10.0.402 / `android` 36.1.118 | 10.0.400 / `ios` 27.1.10018 (Xcode 27.1) |
| Build | 0 warnings, 0 errors; APK 7.8 MB | 0 warnings, 0 errors; app 15.7 MB |
| Checks | 201 ok, 0 failed | 201 ok, 0 failed |
