# Media identity regression checks

From `D:/Deepseek/work`, run `pwsh -File dev_feature/streamhub/tests/media-trust/Run.ps1`.
The runner snapshots the actual overlay sources, filling unchanged dependencies from the complete candidate, into `D:/Deepseek/build/stage38-tests/media-trust`.

The 57 assertions use generated public certificates, temporary profile state and an in-memory HTTP transport. They cover exact duplicate repair, unrelated identities, disabled authorization, profile isolation, state/API disagreement, deletion readback, partial deletion and retry, CSRF, invalid formats and size, cancellation, the real three-second cooperative budget, and the bound-device HTTP handler's authentication/error contract. The HTTP listener is never started. No installed state, real network, Windows certificate store, ACL, desktop or service is accessed.

The first run omitted the fixture request Content-Type and correctly received HTTP 415; the final fixture uses application/json and all 57 assertions pass. This was a test setup correction. It is not evidence of a product HTTP defect.

These checks establish local behavior only. Actual media mutual TLS, first frame, active sessions and device recovery still require installation testing. The separate local-optimization media-gate checks exercise command admission from the compiled product service using an unconstructed test object, without starting its runtime.
