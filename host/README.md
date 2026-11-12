# StreamHub Host

The host shell owns lifecycle and policy; it does not reimplement video
encoding in the first release.

## Profiles

`RemoteDesktop` points at `C:\Program Files\Sunshine` and the existing
`SunshineService`. It captures the physical primary display and preserves the
normal Realtek audio endpoint.

`TabletSecondScreen` points at `C:\Program Files\Sunshine-Tablet`, port
`48089`, the VDD output GUID, the orientation prep script and disabled stream
audio. It captures the virtual display at the negotiated receiver resolution in
portrait and leaves Windows audio on the laptop speakers.

## Supervisor contract

The eventual Windows tray process must:

- never edit the two profile files while a stream is active;
- start the requested backend and verify its process, port and display target;
- report a useful recovery action instead of silently switching audio/display;
- keep the current startup task and provide an explicit rollback;
- expose only local IPC for controls; streaming ports remain the configured
  LAN/Tailscale ports.

The first implementation should be a .NET 8 WinForms/WPF single-file host. A
machine with the .NET SDK and Windows build tools is required to compile it.
