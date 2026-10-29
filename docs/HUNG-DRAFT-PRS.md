# Draft PRs ready for review

Date: 2026-10-29. All listed branches have been pushed. No PR was created; connector returned 403. No merges performed.

Branches are cumulative and target dev. Merge in the order listed. Later integration branches include additional regression fixes identified during testing. Do not merge all branches simultaneously.

Verification: BE 46/46 automated tests pass; FE build passes; 4/4 browser component scenarios pass. These use EF InMemory and mocked HTTP/MQTT/payment, not production or hardware E2E. SQL script has not been applied to a real database. Real PayOS, ESP32 and relational concurrency remain pending; keep PRs draft.

## SmartLocker_BE

### [Hung] Shared plan and supporting persistence

Branch: `hunglqde180096/uc-foundation` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-foundation?expand=1)

Proposed description:

Implement Shared plan and supporting persistence. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T11 paid-ticket check-in and physical door cycle

Branch: `hunglqde180096/uc-t11` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t11?expand=1)

Proposed description:

Implement UC-T11 paid-ticket check-in and physical door cycle. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T12 pickup authentication and outstanding fees

Branch: `hunglqde180096/uc-t12` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t12?expand=1)

Proposed description:

Implement UC-T12 pickup authentication and outstanding fees. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-S08 camera evidence and staff approval

Branch: `hunglqde180096/uc-s08` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-s08?expand=1)

Proposed description:

Implement UC-S08 camera evidence and staff approval. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T15 guest booking and secret payment session

Branch: `hunglqde180096/uc-t15` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t15?expand=1)

Proposed description:

Implement UC-T15 guest booking and secret payment session. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T16 expiring revocable pickup delegation

Branch: `hunglqde180096/uc-t16` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t16?expand=1)

Proposed description:

Implement UC-T16 expiring revocable pickup delegation. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T17 lost-item reporting and staff response

Branch: `hunglqde180096/uc-t17` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t17?expand=1)

Proposed description:

Implement UC-T17 lost-item reporting and staff response. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T21 station reviews and integrated regression verification

Branch: `hunglqde180096/uc-t21` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_BE/compare/dev...hunglqde180096/uc-t21?expand=1)

Proposed description:

Implement UC-T21 station reviews and integrated regression verification. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t21: 46 backend tests). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

## SmartLocker_FE

### [Hung] UC-T09 automatically refresh expired QR

Branch: `hunglqde180096/uc-t09-refresh` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t09-refresh?expand=1)

Proposed description:

Implement UC-T09 automatically refresh expired QR. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T11 kiosk operation session and sensor confirmation

Branch: `hunglqde180096/uc-t11` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t11?expand=1)

Proposed description:

Implement UC-T11 kiosk operation session and sensor confirmation. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-S02 reserved state and realtime locker grid

Branch: `hunglqde180096/uc-s02` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-s02?expand=1)

Proposed description:

Implement UC-S02 reserved state and realtime locker grid. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-S08 staff camera-evidence approval queue

Branch: `hunglqde180096/uc-s08` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-s08?expand=1)

Proposed description:

Implement UC-S08 staff camera-evidence approval queue. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T15 kiosk walk-in payment and PIN handoff

Branch: `hunglqde180096/uc-t15` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t15?expand=1)

Proposed description:

Implement UC-T15 kiosk walk-in payment and PIN handoff. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T16 pickup delegation interface

Branch: `hunglqde180096/uc-t16` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t16?expand=1)

Proposed description:

Implement UC-T16 pickup delegation interface. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T17 photo report and staff response display

Branch: `hunglqde180096/uc-t17` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t17?expand=1)

Proposed description:

Implement UC-T17 photo report and staff response display. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T21 review interface after completed pickup

Branch: `hunglqde180096/uc-t21` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t21?expand=1)

Proposed description:

Implement UC-T21 review interface after completed pickup. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.

### [Hung] UC-T23 five-language kiosk, final integration and browser verification

Branch: `hunglqde180096/uc-t23` → `dev`

[Open compare / create draft PR](https://github.com/Hunter1995VN/SmartLocker_FE/compare/dev...hunglqde180096/uc-t23?expand=1)

Proposed description:

Implement UC-T23 five-language kiosk, final integration and browser verification. Preserve existing features outside this use case. This branch includes preceding use cases in the sequence above; review its UC-specific commits and merge prerequisites first.

Validation is recorded on the final integration branch (uc-t23: production build and 4 browser component scenarios). Real database migration and hardware/payment acceptance are pending. Keep draft until those checks are complete.


