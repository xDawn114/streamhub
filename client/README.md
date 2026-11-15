# StreamHub Client

The Android deliverable is one APK based on the GPL-3.0 Artemis/Moonlight
codebase. It keeps one host list and one pairing flow, then selects a profile:

- `Remote desktop`: host `xDawn`, app `Desktop`.
- `Second screen`: host `XDawn-Tablet`, app `Tablet Second Screen`.

The first StreamHub mode layer is implemented in `PcView`/`AppView`: tapping a
paired computer asks for the mode, labels the selected mode, and automatically
starts `Desktop` or `Tablet Second Screen` when that app is present on the
host. The host list and pairings remain the existing Artemis database format.

The second-screen profile keeps Artemis v20.2.6 behaviour: native
receiver-negotiated resolution, automatic portrait direction, HEVC hardware decode and
the current 15 Mbps default. The remote profile keeps ordinary Moonlight
controls.

Network probing must try, in order, the saved Wi-Fi/LAN address, a detected
Tailscale 100.64.0.0/10 address, and the USB-tether subnet. ADB is only a
development/launch convenience; it is not the media transport.

The existing `com.limelight` and `com.limelight.noir` data stores cannot be
assumed readable by a new package. The finished APK should offer a one-time
re-pair/migration flow, then the old two apps can be removed after verification.

An Android SDK, Gradle and the required NDK/toolchain are needed before this
module can be built locally.
