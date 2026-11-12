# Third-party notices

StreamHub retains all upstream copyright and license notices. The main GPL-3.0-or-later grant covers Dawn's original StreamHub code and modifications only.

| Component | Origin and pinned source | License and corresponding materials |
| --- | --- | --- |
| Android receiver | [Artemis](https://github.com/ClassicOldSong/moonlight-android), based on Moonlight | GPL-3.0, `client/artemis-src/LICENSE.txt`; individual files preserve upstream attribution |
| Moonlight native streaming, ENet and submodules | `client/artemis-src/app/src/main/jni/moonlight-core/` | Preserve the included licenses for each subcomponent |
| Modified Sunshine | [LizardByte/Sunshine](https://github.com/LizardByte/Sunshine), commit `63d35f702ee9e362e43263742981836ec0710384` | GPL-3.0; exact patch, build record and instructions in `native/`; full corresponding source accompanies the internal Windows installers, not a claimed public binary in this source release |
| Tailscale Android core | [tailscale/tailscale-android](https://github.com/tailscale/tailscale-android), commit `b9d487fe6d7203aeac889aeec2faccb9d32bb701`, Go module `tailscale.com v1.104.4` | BSD-3-Clause; LICENSE, PATENTS, build script and AAR hash in `client/network-core/` |
| Tailscale Windows and Wintun | Official upstream components used by the Windows packaging script | Retain their original licenses/signatures; installer includes the corresponding license/source materials. These binaries are not checked into this repository |
| Virtual Display Driver | MTT upstream driver used by Windows packaging | Retain upstream licensing and original driver signatures; do not re-sign it as StreamHub |
| .NET, AndroidX and other build dependencies | NuGet/Gradle declarations and upstream source trees | Their original notices and package license terms continue to apply |

This initial public release contains product source and source/build references. It does not provide a signed Windows installer, claim SignPath approval, or relicense upstream code. Before publicly distributing bundled binaries, publish their exact corresponding source and required notices alongside them. No third-party binary is signed using the project's identity without separate scope and eligibility review.
