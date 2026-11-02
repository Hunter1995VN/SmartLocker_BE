# UC-T12 — real kiosk pickup and ticket revocation

31/10/2026, dependent on T09/T11. The owner paid 15,000 VND through PayOS, physically deposited luggage and then retrieved it from the actual S compartment.

Final verification: 93/93 backend tests pass (including T09/T11 and T17/T21 regression); frontend TypeScript and production build pass; UTC regression passes. Targeted lint reports warnings and no errors. Existing bundle-size/dynamic-import warnings remain.

- A current QR ticket was entered into the production Kiosk QR input. API authorization sent MQTT to the correctly mapped ESP32. Optical-camera decoding was not exercised.
- SQL while the door was OPEN remained STORED/OCCUPIED; CheckedOutAt recorded the pending operation. After the user removed luggage and closed the compartment, SQL became COMPLETED/AVAILABLE/CLOSED. CheckedOutAt matched the physical CLOSED event at 15:31:09 Vietnam time; the credential was inactive and revoked. Payment remained PAID, amount unchanged. No overdue fee arose in this early-pickup case.
- Actual API checks rejected stale/rotated QR, wrong station, another deposit on the STORED booking and owner web checkout bypass. After completion, four QR/PIN check-in/check-out replays were rejected. Completed ticket APIs hid all credentials and refused offline reissuance. The separated integration API also rejected all four replays.
- The production ticket page updated to COMPLETED, hid QR/PIN and disabled copy/offline export. The separated Kiosk UI uses real stations and status, with all quick-demo credentials, static compartment codes and interactive simulated doors removed. It cannot complete an operation without the backend's session-bound completion result.
- Isolated tests cover expiry/revocation/auth lock, stale/offline device, MQTT failure, duplicate pickup, outstanding overdue amount minus previous payments, exact credential-bound fee sessions and QR expiry during payment. A payment redirect alone cannot unlock a compartment; the frontend waits for server reconciliation. Paying an overdue fee with a real bank transfer was not needed or exercised in this case.
- The unsafe anonymous customer manual-confirm shortcut was removed. A missing door confirmation routes to staff assistance; UC-S08 camera/staff approval is a separate, unaccepted follow-up, not an automatic success path in this release. Delegation and Walk-in changes are excluded.
- A later final-state read found the empty door OPEN again; the user closed it and actual API confirmed AVAILABLE/CLOSED. Reuse is additionally guarded: an open compartment or one belonging to another active booking cannot be assigned to a new deposit.

Evidence in validation/: uc-t12-pending.json, uc-t12-completed.json, uc-t12-boundaries.json and uc-t12-replay.json. These contain state/timestamp assertions only; no PINs, bearer QR values, private account/booking IDs or credentials.
