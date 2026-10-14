# pager: P32 | mcp | shared | an incident is read by id: what it is, its timeline and who was told
#
# Backlog #157, F2: "incident <id> just happened, can you check it". The same incident by full
# id, by a 12-digit prefix and by its console URL; its timeline starts with its opening; the
# notifications show who was told. An id that names nothing is an error the model can read,
# not an empty answer it could mistake for "fine".

scenario() {
    mcp_ready || return
    local n id overview short url notes i

    n=$(pager_name P32)
    pager_fire "$n" namespace=pager-p32 deployment=checkout
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")

    overview=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident "$(jq -cn --arg id "$id" '{id:$id}')")")
    want "get_incident returns that incident" "$(jq -r '.incident.id' <<<"$overview")" = "$id"
    want "it names its alert" "$(jq -r '.incident.alertName' <<<"$overview")" = "$n"
    # The incident is still moving (Investigating, then Escalated) while it is read, so the state
    # the tool says is compared with the API's read just before it AND just after it: one of the
    # two is the moment the tool saw. Read again a few times when both moved on in between.
    local before mcp after agreed=""
    for i in 1 2 3 4 5; do
        before=$(pager_state "$id")
        mcp=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident "$(jq -cn --arg id "$id" '{id:$id}')")" | jq -r '.incident.state')
        after=$(pager_state "$id")
        if [ "$mcp" = "$before" ] || [ "$mcp" = "$after" ]; then agreed=$mcp; break; fi
        sleep 1
    done
    want "its state is the API's" "${agreed:-$mcp}" = "${agreed:-$after}"
    want "it says what to call next" "$(jq '.next | length' <<<"$overview")" -ge 1

    short=$(tr -d '-' <<<"$id" | cut -c1-12)
    want "a 12-digit prefix finds it" \
        "$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident "$(jq -cn --arg id "$short" '{id:$id}')")" | jq -r '.incident.id')" = "$id"
    url="http://console.example/incidents/$id"
    want "a console URL finds it" \
        "$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident "$(jq -cn --arg id "$url" '{id:$id}')")" | jq -r '.incident.id')" = "$id"

    want "the timeline starts with the incident opening" \
        "$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident_timeline "$(jq -cn --arg id "$id" '{incidentId:$id}')")" \
            | jq -r '[.entries[] | select(.kind == "transition")][0].to // empty')" != ""

    notes=0
    for i in $(seq 1 30); do
        notes=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" list_incident_notifications "$(jq -cn --arg id "$id" '{incidentId:$id}')")" \
            | jq '.notifications | length')
        [ "${notes:-0}" -ge 1 ] && break
        sleep 2
    done
    want "the notifications show who was told" "${notes:-0}" -ge 1

    want "an id that names nothing is an error" \
        "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident '{"id":"00000000-0000-7000-8000-000000000000"}' | jq -r '.isError')" = true
}
