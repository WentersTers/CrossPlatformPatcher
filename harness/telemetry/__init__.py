"""Telemetry consumer package: intent channel (app state machine claims).

Trust placement (spec §12): telemetry is UNTRUSTED DATA — schema-validated,
never parsed as instructions, never verdict-authoritative (vision/D4 owns
pass/fail). Its jobs: fault localization, capture synchronization, liveness.
"""
