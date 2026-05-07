# Time-window gate for Vais.Agents.
#
# Strategy: allow Invoke only during business hours (09:00–17:00 UTC,
# Monday–Friday). Deny all other times with a structured reason.
# Suited for cost control, compliance windows, or batch-job separation.
#
# Scope: gates the Invoke operation only — Create / Update / Signal
# are permitted at any time. Adjust `gated_operation` to widen.

package vais.agents

default allow := {"allowed": true}

allow := {"allowed": false, "reason": "invocations blocked outside business hours (09:00–17:00 UTC Mon–Fri)"} if {
    gated_operation
    not business_hours
}

gated_operation if { input.operation == "Invoke" }

business_hours if {
    [h, _, _] := time.clock([time.now_ns(), "UTC"])
    h >= 9
    h < 17
    weekday := time.weekday(time.now_ns())
    weekday != "Saturday"
    weekday != "Sunday"
}
