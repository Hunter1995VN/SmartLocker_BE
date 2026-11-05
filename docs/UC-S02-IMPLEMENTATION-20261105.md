# UC-S02: consistent reservation, occupancy and hardware state

Accepted locally against actual paid S booking, shared SQL and ESP32. Ready for isolated branch publication and integration checks; do not merge main.

- Keep AVAILABLE, RESERVED, OCCUPIED and MAINTENANCE distinct. Gray maintenance and yellow reservations; retain BLOCKED/DISABLED and fail closed on unknown values.
- Kiosk displays mapped device compartments. Admin displays the complete configured inventory, explicitly marking unmapped rows. No generated cabinet positions, fake diagnostics, fixed IP, weight, voltage, latency or firmware claims.
- Both views read the same per-size order aggregate: live pending payments, confirmed orders awaiting deposit, unassigned confirmed orders and stored orders. A size reservation does not invent a compartment assignment; specific RESERVED/OCCUPIED cards reflect the actual locker row.
- Each Admin grid owns its SignalR connection, re-reads after door/unlock/connectivity/status events and after reconnect, and polls every five seconds. Failed reads remove stale actionable data; station changes cancel stale results. Kiosk uses the same events and fallback.
- Administrative status updates cannot fabricate RESERVED/OCCUPIED, free an active order, or make unhealthy/open/unknown compartments available. Require an audit reason; transaction and notification follow persistence. Actual emergency-unlock errors are reported as failures.
- No schema updates, fake payments, synthetic MQTT door events, existing debt resets or main changes.

Validation: 129 backend tests pass (119 prior regressions plus ten grid/status/reservation/snapshot cases); TypeScript and production build pass. Targeted frontend lint reports no warnings or errors. Existing backend nullable and production bundle/dynamic-import warnings remain.

Actual acceptance on 2026-11-05:
- A real unpaid TEST order appeared on both screens and was cancelled through the actual API; both pending counts returned to zero. No payment or unlock was fabricated.
- A separate S order was genuinely paid via PayOS (15,000 VND). Both views showed one confirmed/unassigned size reservation. Check-in assigned the actual S compartment, displayed RESERVED on both, then actual OPEN -> CLOSED produced STORED/OCCUPIED and one stored order. Pickup with the genuine current QR payload and actual OPEN -> CLOSED produced COMPLETED/AVAILABLE and zero stored orders on both.
- Paid confirmation 02:29:21 UTC; deposit actual opening 02:33:55.277 and closure 02:34:04.686; final pickup closure 02:37:46.869. Purchased one-hour duration starts at the actual deposit opening. Ticket revoked and owner read hides PIN/online QR/offline credentials. Four old credential replays (PIN/QR x deposit/pickup) returned HTTP400.
- Actual reversible maintenance on the empty S bay was visible on both screens and restored to AVAILABLE/HEALTHY. Observer-label issues were corrected and a complete UI maintenance/restore round-trip passed. No fake diagnostic or hard-coded telemetry values remain in the grid/detail view.
- Stopping the owned local broker after all luggage was retrieved produced OFFLINE on both views after the configured heartbeat timeout, with public availableS=0. Restarting it restored actual ONLINE. Empty-door heartbeat snapshots may resynchronize idle bays only; snapshots never complete a booking or advance a real OPEN/CLOSED transition timestamp. Duplicate size mappings are not arbitrarily selected.
- Controlled offline/online in the separate browser context cleared stale rows in both views and recovered actual API data without fixtures. Browser page errors: none.
- Browser errors in the recorded paid flow: none. M/L business states remained AVAILABLE with actual OPEN sensor readings. Do not claim all three compartments are CLOSED/bookable.

Evidence: docs/validation/uc-s02-actual-acceptance-20261105.json contains sanitized state summaries. Raw tickets, payment links, credentials, serial logs, SQL IDs and screenshots remain ignored under API/App_Data/private-validation. No schema changes, debt resets, main push or merge.

S02 acceptance does not accept S08/T15/T16/T23. Optical-camera scan and actual overdue transfer were not performed. Four later local checkpoints are documented in CLAUDE-HANDOFF-HUNG-20261105.md and must be integrated with the final S02 fixes before their own acceptance.
