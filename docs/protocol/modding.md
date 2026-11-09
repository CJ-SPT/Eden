# Modding protocol and activation

## Package foundation — November 9, 2026

Version-1 portable packages and host composition retain the existing build-3662168 skill overlay and immutable
client archive boundary. No new native RPC or notification is introduced in this milestone. Shared skill validation
now resides in Gameplay and consumes typed catalog metadata, retaining protected original skill calculations.

Permanent logical/numeric skill mappings and namespaced mod state reside in World-owned SQLite persistence.
Restart activation verifies composed files and retains an interrupted-activation recovery marker. See the
[modding guide](../modding.md#foundation-validation--november-9-2026) for commands, source references and automated evidence.

Live acceptance: Not performed; agent client testing is disabled.
