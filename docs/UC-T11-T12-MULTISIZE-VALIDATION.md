# S/M/L expansion — 2026-11-02

Status: paid M and L deposit/pickup accepted on real hardware; final integration with current dev is in progress. Do not merge into main.

## Findings and changes

- Shared SQL initially mapped only SIM-MQTT-01 (S). Firmware M/L GUIDs did not exist in SQL. Reused the existing, correctly sized BT-M-01 and BT-L-01 inventory rows; no extra fictitious compartments were inserted.
- Guarded mapping script verifies station/device/size/health, prevents conflicting GPIO mappings and refuses lockers owned by active bookings. Only mapping metadata changes. Newly mapped doors become UNKNOWN until real ESP32 telemetry arrives. No booking, payment, health or business status is fabricated.
- Relay S/M/L uses GPIO26/25/33; door switches use GPIO27/32/23. Firmware now routes commands by an exact locker GUID; code substrings such as '-M' inside SIM-MQTT can no longer select a relay. Missing, unknown and duplicate GUID mappings fail closed.
- Canonical Arduino sketch is under iotDevice/SmartLocker_ESP32_SingleFile. Other standalone .ino files at the modular sketch root are disabled to prevent duplicate setup/loop definitions.
- Booking creation, public station availability and dynamic check-in assignment share the same hardware eligibility rules: correct station/size, real device mapping, fresh online heartbeat, healthy/available compartment and closed door. Only live, unassigned size reservations are subtracted from that remaining pool; assigned lockers are not subtracted twice. No automatic size downgrade/upgrade occurs.
- Station totals and compartment codes come from device-mapped inventory, rather than cached station totals or generated visual positions. Effective block pricing is returned per size. Frontend rounds up by configured block length and blocks missing/invalid policies.
- Kiosk displays all configured S/M/L compartments with business, health, connection and actual door states. SignalR and a five-second poll refresh data; failed reads clear the stale view. Connection lights reflect the response. Clicking a displayed compartment does not unlock it.
- LAN broker uses the locally saved device credentials, requires authentication outside loopback, and restricts the ESP32 to its own door/heartbeat publishing and command subscription. Loopback is reserved for the local API. No payload or password logging is enabled in the broker.
- IoT headers, App_Data (keys, raw tickets, backup and private validation), and dependency directories are Git-ignored.

## Validation completed

- Backend: 111/111 tests pass, including independent S/M/L deposit/pickup cases, wrong-size rejection, cross-compartment event isolation, ticket revocation, capacity and existing UC regressions. Test fixtures/events only use isolated EF InMemory; they never connect to the shared DB or broker.
- Frontend: TypeScript and production build pass; three quote/UTC tests pass. Existing bundle/dynamic-import warnings remain.
- Broker: authentication/topic ACL tests pass (2/2).
- Firmware: ESP32 3.2.0 / esp32:esp32:esp32 compile passes; 930,208-byte app flashed on the real ESP32 at COM5, with esptool hash verification. A verified 4,194,304-byte pre-update backup was read in 64 blocks and retained only in ignored local storage.
- Actual ESP32 connected to the newly selected Wi-Fi and authenticated MQTT broker. API/shared SQL received real events for S, M and L. Initial post-flash snapshot: S CLOSED, M CLOSED, L OPEN; connection ONLINE. Later events may change these states; use live API before every operation.
- Actual public API returns configured totals S=1/M=1/L=1. OPEN/offline/unknown compartments are unavailable. Station list completed in approximately 1.36 seconds against the shared DB after batching queries.
- Production booking/Kiosk components were exercised with Edge headless and the real API, with no intercepted responses or simulated sensors. Correct actual codes were rendered, and no JavaScript page errors occurred. Desktop Computer Use runtime could not initialize, so no live desktop-browser interaction is claimed.
- Traveler booking attempt was correctly refused because the provided account still has an overdue STORED booking from 2026-10-29. No M/L booking or payment link was created, and no debt/status was reset. A different eligible Traveler account was subsequently supplied and verified with no existing bookings; the blocked account and its overdue booking remain unchanged.

## Acceptance procedure and release order

1. Eligible Traveler verified. User authorized separate M/L one-hour real payment links and paid both (M 25,000 VND; L 40,000 VND).
2. Confirm the selected compartment is CLOSED/ONLINE and unused immediately before creating each order. Create M first; do not create both links early or ask for payment for an unusable door.
3. Verify real PayOS PAID and an eight-digit ticket. Use the exact paid booking/size to check in; observe the correct physical relay and OPEN then CLOSED. SQL must become STORED/OCCUPIED only for that compartment.
4. Check out with the same ticket/assigned compartment. Observe real OPEN then CLOSED after the luggage is removed; verify COMPLETED/AVAILABLE, revoked credentials and denied PIN/QR replays. Confirm other sizes' bookings and physical doors were not changed.
5. Repeat for L, including a negative check that an unavailable selected size is refused rather than opening S or M.
6. Publish/merge only after acceptance completes and evidence is sanitized. Keep separate UC scope and do not merge into main.

Private account, payment, ticket, serial and backup artifacts remain under API/App_Data and must never be committed. Hardware connection or an automated unit-test result alone does not constitute paid M/L business acceptance.

## Live acceptance checkpoints (UTC)

- M: real PayOS PAID at 14:17:04.294; protected eight-digit PIN issued without replacing a legacy ticket. Production Kiosk keypad accepted check-in for BT-M-01. Actual opening 14:21:06.873; physical closure 14:21:42.385 produced STORED/OCCUPIED. StartAt equals opening; EndAt is exactly one hour later.
- M: production QR input accepted the current online payload for checkout. Actual final closure 14:25:49.514 produced COMPLETED/AVAILABLE and credential revocation. UI displayed success. Later empty-door activity is recorded independently; a subsequent API read confirmed M CLOSED. All four old-PIN/QR check-in/out replays returned HTTP400.
- Fixed a live-discovered frontend race: a quick QR submission could be rejected locally while the independent station discovery list was still loading. The exact requested station is now validated by the kiosk API, rather than that asynchronous UI cache. A submission without a station remains refused.
- Added backend guards refusing checkout for OPEN/UNKNOWN doors or a mismatched assigned size; three additional regression cases pass. The API was restarted only between completed M and the L case, with no pending physical operation.
- L: the production StationBookingPage was used to select L/one hour and create the real PayOS link; server amount 40,000 VND. PAID confirmed at 14:35:09.859. Production LoginPage/ticket/Kiosk verified eight-digit PIN and accepted check-in to BT-L-01. UART recorded relay L ON/OFF at approximately 14:36:03.889–14:36:04.371; actual door OPEN at 14:36:04.473. SQL remained CONFIRMED/RESERVED pending physical closure. Physical closure at 14:44:57.870 produced STORED/OCCUPIED. StartAt remains the first actual OPEN (14:36:04.473), and EndAt is exactly one hour later. The headless check-in observer had reached its four-minute test timeout before this manual closure; the backend continued normally and SQL confirms storage, but a live check-in success screen is not claimed for that delayed observer.
- QR acceptance above uses the production text/wedge input with a real current payload; an optical-camera scan is not claimed. These were early pickups, not a live overdue surcharge transfer.- L: fresh production QR input accepted checkout at approximately 14:46:15. UART confirmed relay L ON/OFF; user retrieved the luggage. Final physical closure at 14:47:19.652 produced COMPLETED/AVAILABLE, IsActive=false and RevokedAt set. Production Kiosk checkout success was observed.
- L: all four old-PIN/QR check-in/out replays returned HTTP400. Owner booking reads for both M/L hide PIN, online QR and offline credentials after completion.
- Final actual station read after the user's last closure: S/M/L all AVAILABLE, CLOSED and ONLINE. Door states remain live telemetry, not fixed display values.
- Both real paid cases were early pickups. No optical camera scan, actual overdue bank transfer, lost-and-found incident or staff camera fallback is claimed in this expansion.