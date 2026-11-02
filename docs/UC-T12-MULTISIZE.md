# UC-T12: pickup from the assigned M/L compartment

Depends on the UC-T11 S/M/L mapping and common kiosk changes. Pickup verifies that the assigned compartment matches the booking size and has a CLOSED door before issuing another pulse. OPEN/UNKNOWN and mismatched-size cases refuse the request; no other-size fallback is allowed.

Live acceptance on 2026-11-02: current QR payloads from the production ticket page were entered into the production kiosk QR/wedge input. M and L each opened the correct assigned compartment. After the user removed luggage and closed the physical door, SQL became COMPLETED/AVAILABLE and revoked the credential. M closure: 14:25:49.514 UTC; L closure: 14:47:19.652 UTC. UART explicitly captured L relay ON/OFF on both deposit and pickup. Both production checkout success screens were observed.

Each booking rejected all four old-PIN/QR check-in/out replay attempts (HTTP400). Owner booking reads hide PIN/online QR/offline credentials after completion. The final station read confirmed all S/M/L AVAILABLE/CLOSED/ONLINE; subsequent telemetry can change this.

A live frontend race was also fixed: an immediate QR submission no longer depends on the separately loading discovery list. The kiosk API still validates the exact station, booking, device and credential. This does not add an alternate authorization or a simulated success path.

Final local checks: 111/111 backend tests, two broker ACL/auth tests, three frontend quote/UTC tests, TypeScript and production build pass. Existing React/bundle warnings remain. No optical-camera scan or actual overdue surcharge transfer was performed. Details and anonymized state evidence are in UC-T11-T12-MULTISIZE-VALIDATION.md and docs/validation. Do not publish account, bank, ticket, token, serial-raw or flash data, and do not merge into main.
