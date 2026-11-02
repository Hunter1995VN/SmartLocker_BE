# UC-T11: three actual compartment sizes

The prototype initially had only S mapped to SQL; M/L firmware IDs were nonexistent. The existing BT-M-01 and BT-L-01 inventory rows now map to the real controller, without inserting fictitious compartments. Routing uses exact GUIDs. S/M/L relay GPIOs are 26/25/33 and door switches 27/32/23.

Booking availability and check-in assignment share ready-device/closed-door/healthy/size checks. Assigned capacity is not subtracted twice; an unavailable size is refused. Public totals and the kiosk grid reflect device-mapped SQL inventory. Prices use the effective per-size block policy, with no invented fallback price/count/compartment code on the booking page.

Real acceptance on 2026-11-02: the user paid M 25,000 VND and L 40,000 VND for one hour each. Both received protected eight-digit tickets. Production PIN keypad check-in reached the correct physical relay, then real OPEN/CLOSED produced STORED/OCCUPIED for that compartment only. M opened at 14:21:06.873 UTC, L at 14:36:04.473 UTC; each StartAt matches its opening and EndAt is one hour later. SQL evidence is under docs/validation/multisize-*-stored.json. S had already passed its paid physical case on 2026-10-31; this expansion additionally verified its mapped identity and live sensor, not another paid S cycle.

Automated foundation/regression tests: 108 backend cases, per-size block-price/UTC frontend tests, TypeScript/build, firmware compile/flash verification, and broker credential/ACL tests. The final integrated package adds three UC-T12 guards (111 backend cases). L's check-in observer timed out before the user's delayed closure; SQL subsequently confirmed STORED. No claim is made that that expired observer displayed success.

Persist secrets/flash backups only in ignored local storage. See the canonical sketch README and Map-PrototypeCompartments.ps1 (read-only by default). Merge this shared foundation before UC-T12 follow-up. Main remains outside the authorized release scope.
