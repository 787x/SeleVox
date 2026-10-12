# EdgeTTS.DotNet local adaptation

Source: https://github.com/twn39/EdgeTTS.DotNet/tree/v0.4.0
Commit: bf43ca26d5db85e856fb16f4fc0e1b83dab424e3 (MIT, LICENSE alongside).
The pinned NuGet package supplies public configuration, voice/chunk models and exceptions.
Only transport/constants/token formatting are adapted here, not a new protocol port.
Changes: restrict retries, remove clock correction and identity rotation; dispose failed
handshakes; require turn.end and throw on cancellation; supply one safely pre-split
text part per request; use invariant token formatting. No subtitle/export API.
Only initial transport failures retry once, with the same URI/identity, before any text is sent; no retry after audio or refusal. HTTP and WebSocket response/message sizes are bounded.
Voice query uses the same constants and releases HTTP response/client on all paths.
The library license does not grant Microsoft service access or availability guarantees.

Initial Cookie and permessage-deflate options match the upstream normal handshake.
MUID is created once per process and shared by HTTP/WS requests; it never rotates
after refusal or during retry.
