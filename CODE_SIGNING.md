# Code signing policy

Status: **application submitted on 2026-11-12; awaiting review, with no SignPath approval or signed StreamHub release yet** (status recorded 2026-11-13).

The project is applying for free open source code signing through [SignPath.io](https://signpath.io) and the [SignPath Foundation](https://signpath.org). The attribution “Free code signing provided by SignPath.io, certificate by SignPath Foundation” will apply only after acceptance and actual signing; it is not a claim about the current unsigned internal candidates.

## Team and approval

- Author/committer: Dawn ([xDawn114](https://github.com/xDawn114)).
- Reviewer for external contributions: Dawn. External pull requests require explicit review before becoming release inputs.
- Signing approver: Dawn. Every release must receive manual approval; CI does not automatically authorize a signing request.
- Multi-factor authentication for GitHub and SignPath is required before signing access. This document does not assert that an unverified account setting has already been configured.

## Allowed artifacts and source origin

Signing is limited to the project's Windows app (`我控制.exe`), service (`串流中心后台.exe`), uninstaller (`卸载串流中心.exe`) and installer (`我控制-电脑端安装.exe`) built from a reviewed, fixed public source commit using trusted CI.

Third-party binaries keep their upstream identity and license. No third-party binary is re-signed with the StreamHub project's identity. Modified Sunshine builds need separate review against the Foundation's visible-fork and upstream-branch requirements.

The public `Source checks` workflow compiles the own Windows projects and runs input policy checks. It is **not** a complete installer or signing pipeline. Before actual signing, configure the approved SignPath project/policy, artifact restrictions, product/version metadata, fixed publisher trust, signing order and independent verification. Signing secrets must never be passed to untrusted pull requests or committed to source.

Current gaps include the project's short public history, a public release of the complete binary form intended for signing, approved signing-service configuration, and verified publisher identity. Foundation acceptance is not guaranteed.

## Privacy

See [PRIVACY.md](PRIVACY.md), including the third-party Tailscale privacy policy. Passwords, binding tokens, personal logs and Android release private keys are not published or sent with the signing application.
