## What does this change?

<!-- One paragraph. What changes for a user of the receiver or a client? -->

## Type of change

- [ ] Bug fix
- [ ] New capability
- [ ] Protocol change ⚠️
- [ ] Refactor / internal
- [ ] Docs

## Protocol impact

<!--
If the wire protocol changes, say so here and list EVERY component touched.
A protocol change must land in the server and BOTH clients in the same PR.
See docs/PROTOCOL.md.
-->

- [ ] No protocol change
- [ ] Protocol change — components updated together:
  - [ ] `server-dotnet/`
  - [ ] `client-dotnet/`
  - [ ] `client-python/`
  - [ ] `contracts/openapi.yaml`
  - [ ] `docs/PROTOCOL.md`

## How was this verified?

<!--
Paste the real commands and their output. Examples:
  dotnet test aurora-file-bucket.sln
  python -m pytest client-python/tests -q
  python scripts/e2e.py
-->

## Checklist

- [ ] `dotnet build aurora-file-bucket.sln` succeeds
- [ ] `dotnet test aurora-file-bucket.sln` passes
- [ ] `python -m pytest client-python/tests -q` passes
- [ ] `python scripts/e2e.py` passes (if the change touches the protocol or the transfer flow)
- [ ] No secrets, credentials or chunk payloads are included in the diff
