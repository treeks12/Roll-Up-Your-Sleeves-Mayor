# Decision-event checks

Console checker (net10) for the sprint-06 decision layer in `SC4Evolution.Core.Decisions`.
References the actual Core project; nothing native executes and no UI/simulation buffer
is read — the layer consumes published values only.

Coverage: event lifecycle (creation, duration, id assignment); cooldown suppression (the
same cause cannot re-alert per frame; evidence updates still land while suppressed);
duration expiry (unobserved events drop; persisting conditions refresh their window
instead of silently disappearing); recovery by disappearance (condition ending removes
the event; recurrence is a new id); conflict detection (two departments over one target
with different consequences both get the opportunity-cost mark; same advisor or same
consequence never conflicts); notice resolution (title/message built with the action,
indicator with current and expected values, target shown only with valid identity,
city-wide notices carry an empty target); stale-notice rejection (snapshot frame
predating the last alert, foreign event ids, dead events); codec round-trip
(byte-identical re-save), rejection of truncation/corruption/trailing bytes; restored
engines keep cooldown memory (a reloaded cabinet cannot re-spam); validation;
determinism across periodic conditions.

Run from the repository root:

```powershell
dotnet run --project tests/SC4Evolution.Decisions.Tests -c Release
```
