# Renders Claude Code's stream-json output as a live, human-readable Actions log.
# Used by evolve.yml: claude -p ... --output-format stream-json --verbose | jq -rR --unbuffered -f this-file
# Input is one raw line per event (-R); non-JSON noise lines pass through untouched.
. as $raw | try (
  fromjson |
  (if .parent_tool_use_id then "  ↳ " else "" end) as $indent |
  if .type == "system" and .subtype == "init" then
    "▶ session started (model " + (.model // "?") + ")"
  elif .type == "assistant" then
    (.message.content[]? |
      if .type == "text" and (.text // "") != "" then
        $indent + "💬 " + .text
      elif .type == "tool_use" then
        $indent + "🔧 " + (.name // "?") + ": "
          + ((.input.description // .input.file_path // .input.pattern // .input.query // .input.command // .input.prompt // "")
             | tostring | gsub("\n"; " ") | .[0:200])
      else empty end)
  elif .type == "result" then
    "■ " + (.subtype // "done")
      + " — " + ((.num_turns // "?") | tostring) + " turns"
      + ", $" + ((.total_cost_usd // 0) * 100 | round / 100 | tostring)
      + "\n" + (.result // "")
  else empty end
) catch $raw
