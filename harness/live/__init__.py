"""Live-session drivers: scripted VM interaction on real hardware (Session 2+).

These run ON THE HOST against real guests through virsh/QMP. Agents stay
scripted through calibration (runbook): every action is deterministic,
ledgered, and logged — the QMP send log plus screenshots are the artifacts
that adjudicate each session. No LLM in this package, ever.
"""
