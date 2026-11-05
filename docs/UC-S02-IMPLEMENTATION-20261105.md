# UC-S02: consistent reservation, occupancy and hardware state

Local implementation and software checks complete; physical and paid-reservation acceptance remains in progress. Not a main release.

- Keep AVAILABLE, RESERVED, OCCUPIED and MAINTENANCE distinct. Gray maintenance and yellow reservations; retain BLOCKED/DISABLED and fail closed on unknown values.
- Kiosk displays mapped device compartments. Admin displays the complete configured inventory, explicitly marking unmapped rows. No generated cabinet positions, fake diagnostics, fixed IP, weight, voltage, latency or firmware claims.
- Both views read the same per-size order aggregate: live pending payments, confirmed orders awaiting deposit, unassigned confirmed orders and stored orders. A size reservation does not invent a compartment assignment; specific RESERVED/OCCUPIED cards reflect the actual locker row.
- Each Admin grid owns its SignalR connection, re-reads after door/unlock/connectivity/status events and after reconnect, and polls every five seconds. Failed reads remove stale actionable data; station changes cancel stale results. Kiosk uses the same events and fallback.
- Administrative status updates cannot fabricate RESERVED/OCCUPIED, free an active order, or make unhealthy/open/unknown compartments available. Require an audit reason; transaction and notification follow persistence. Actual emergency-unlock errors are reported as failures.
- No schema updates, fake payments, synthetic MQTT door events, existing debt resets or main changes.

Validation: 128 backend tests passed (119 prior regressions plus nine grid/status/reservation cases); TypeScript and production build passed. Existing nullable/bundle warnings and one effect-state lint warning remain. Actual shared SQL/API and production Admin/Kiosk components were reached through genuine login. Actual reversible TEST maintenance was visible on both; original AVAILABLE/HEALTHY/CLOSED state was restored through the API after an observer label failure, and the label issue was corrected. A repeated successful round-trip and real pending/cancellation/door observations are being collected separately in private App_Data.

Do not call CONFIRMED/STORED live UI acceptance passed until a genuine paid order demonstrates those states in both views. Isolated unit fixtures verify transitions without writing fake business states into shared SQL.
