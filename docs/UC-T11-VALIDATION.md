# UC-T11 — actual kiosk deposit

31/10/2026: one real, owner-paid one-hour S booking was deposited through the production Kiosk component and real API/MQTT/ESP32. No booking status or sensor event was fabricated.

- The owner-authorized T09 eight-digit PIN passed credential lookup at the matching station. The real assigned slot was SIM-MQTT-01; only its actual device mapping was used.
- After the unlock command and physical OPEN event, SQL remained CONFIRMED/RESERVED. A command acknowledgment alone did not complete the booking.
- The user placed luggage in the physical compartment and closed it. SQL then showed STORED/OCCUPIED/CLOSED. CheckedInAt and StartAt matched the actual OPEN event (15:20:52 Vietnam time), with EndAt exactly one purchased hour later. Base payment remained PAID at 15,000 VND.
- Isolated deposit tests cover expired/invalid QR, booking identifiers masquerading as credentials, wrong station, unpaid orders, MQTT failure, duplicate requests, unavailable devices, observed OPEN→CLOSED sequencing and SignalR failure with session-based status polling. Heartbeat snapshots never complete a booking. No-show tests protect pending physical operations/open doors.
- The separated branch reuses the same CheckIn/door-event implementation exercised by the actual hardware run. It adds only the deposit/session/device foundation. T12 is a dependent follow-up and must be integrated before releasing the whole access flow; the previous pickup implementation is replaced there.
- The frontend uses actual station API data, never static fallback stations. Its operation screen displays actual locker code/sensor state, without interactive simulated doors or illustrative price tags. It waits for backend completion and cannot complete a booking from a customer button or a SignalR unlock acknowledgment.

Evidence: validation/uc-t11-pending.json and uc-t11-stored.json. No PINs, QR payloads, account/booking identifiers or connection secrets are included.
