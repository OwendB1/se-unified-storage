---
type: "query"
date: "2026-09-04T23:14:56.700814+00:00"
question: "Why does Shared profile keep Waiting for companion discovery?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["Secure Mod-Message Transport", "Capability Handshake"]
---

# Q: Why does Shared profile keep Waiting for companion discovery?

## Answer

Expanded via graph labels: companion transport message. Graph points to the planned secure mod-message transport, not current runtime behavior. Live Fetch returned NotFound and publication/fetch/adoption succeeded: transport is functional. Source inspection found SharedProfileScreen only updates availability text on failure, leaving its initial waiting label unchanged on success. Fixed locally to update on availability transitions; build passed, in-game reload pending.

## Outcome

- Signal: useful

## Source Nodes

- Secure Mod-Message Transport
- Capability Handshake