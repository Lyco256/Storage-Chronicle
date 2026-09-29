# AgentEventStackProjection.cs

`AgentEventStackProjection` adapts the bounded named-pipe projection contract to the Event Stack UI contract. It forwards mode, page, sort, and semantic filter state, preserves page boundaries, and maps selected-row details without allowing the UI to read the Agent log directly.

Grouped rows and their bounded expansion children are expected to be returned by the Agent in the same page. The adapter retains the source quality, process attribution, reconciliation, and gap markers needed by the detail panel. Its tests belong to the Event Stack and headless UI test projects and cover mode switching, filtering, paging, expansion, and disconnected-agent errors.
