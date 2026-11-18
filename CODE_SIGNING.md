# Code signing policy

Status: **application submitted; approval pending; no signed StreamHub release yet**. The application was submitted on November 12, 2026, and followed up on November 18. The maintainer has confirmed that approval has not arrived.

The project is applying for free open source code signing through [SignPath.io](https://signpath.io) and the [SignPath Foundation](https://signpath.org). The attribution “Free code signing provided by SignPath.io, certificate by SignPath Foundation” will apply only after acceptance and actual signing; it is not a claim about the current unsigned internal candidates.

## Team and approval

- Author/committer: Dawn ([xDawn114](https://github.com/xDawn114)).
- Reviewer for external contributions: Dawn. External pull requests require explicit review before becoming release inputs.
- Signing approver: Dawn. Every release must receive manual approval; CI does not automatically authorize a signing request.
- Multi-factor authentication for GitHub and SignPath is required before signing access. This document does not assert that an unverified account setting has already been configured.

## Allowed artifacts and source origin

Signing is limited to the project's Windows app (`我控制.exe`), service (`串流中心后台.exe`), uninstaller (`卸载串流中心.exe`) and installer (`我控制-电脑端安装.exe`) built from a reviewed, fixed public source commit using trusted CI.

Third-party binaries keep their upstream identity and license. No third-party binary is re-signed with the StreamHub project's identity. Modified Sunshine builds need separate review against the Foundation's visible-fork and upstream-branch requirements.

The public `Source checks` workflow compiles the own Windows projects and runs isolated checks. The separate, manually dispatched `Windows signing` workflow defaults to configuration checking and unsigned program builds. Its production path signs the three owned programs before packaging, then separately signs and verifies the installer and embedded payload. Actual signing still requires approved SignPath configuration and a separately verified publisher committed to the client trust. See [integration instructions](docs/SignPath接入.md). Signing secrets must never be passed to untrusted pull requests or committed to source.

The complete unsigned 182 fix145 candidate is now publicly available with its source and hashes. Remaining external requirements include approved signing-service configuration and verified publisher identity; successful production signing and an actual Windows in-app upgrade have not been demonstrated. Foundation acceptance is not guaranteed.

## Privacy

See [PRIVACY.md](PRIVACY.md), including the third-party Tailscale privacy policy. Passwords, binding tokens, personal logs and Android release private keys are not published or sent with the signing application.
